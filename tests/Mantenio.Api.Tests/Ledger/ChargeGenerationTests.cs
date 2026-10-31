using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.Ledger;

/// <summary>
/// Monthly charge generation and the opening debt import. Generation touches every apartment, so the tests run
/// in order on their own database and each uses its own month.
/// </summary>
public sealed class ChargeGenerationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly decimal Fee = CondoProfile.Current.Billing.MonthlyFee;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int NextPeriod => factory.Services.GetRequiredService<CondoClock>().CurrentPeriod.Next().Value;

    private static async Task<GenerateChargesResult> GenerateAsync(HttpClient founder, int period)
    {
        var response = await founder.PostAsJsonAsync("/api/admin/charges/generate", new GenerateChargesRequest(period), Ct);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GenerateChargesResult>(Ct))!;
    }

    /// <summary>A brand-new apartment: no charges from months other tests already generated.</summary>
    private async Task<(HttpClient Resident, Guid ApartmentId, string Code)> NewApartmentWithResidentAsync(HttpClient founder, string email)
    {
        var towerResponse = await founder.PostAsJsonAsync("/api/admin/towers", new CreateTowerRequest("T" + Guid.NewGuid().ToString("N")[..6]), Ct);
        await towerResponse.ShouldBeAsync(HttpStatusCode.OK);
        var tower = (await towerResponse.Content.ReadFromJsonAsync<TowerItem>(Ct))!;
        var apartmentResponse = await founder.PostAsJsonAsync("/api/admin/apartments", new CreateApartmentRequest(tower.Id, "101"), Ct);
        await apartmentResponse.ShouldBeAsync(HttpStatusCode.OK);
        var apartment = (await apartmentResponse.Content.ReadFromJsonAsync<ApartmentAdminItem>(Ct))!;
        var resident = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, email, apartment.Code));
        return (resident, apartment.Id, apartment.Code);
    }

    private Task<List<Charge>> ChargesAsync(int period) =>
        factory.WithDbAsync(db => db.Charges.Where(c => c.Period == period).ToListAsync(Ct));

    [Fact]
    public async Task Generating_twice_skips_frozen_apartments_and_creates_no_duplicates()
    {
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        var frozen = await founder.ApartmentAsync("I-504");
        var freeze = await founder.PostAsJsonAsync($"/api/admin/apartments/{frozen.Id}/freeze", new FreezeApartmentRequest("Desocupado"), Ct);
        await freeze.ShouldBeAsync(HttpStatusCode.NoContent);
        const int period = 203001;

        var first = await GenerateAsync(founder, period);
        var second = await GenerateAsync(founder, period);

        var apartments = await factory.WithDbAsync(db => db.Apartments.CountAsync(Ct));
        first.Created.ShouldBe(apartments - 1);
        first.SkippedFrozen.ShouldBe(1);
        second.Created.ShouldBe(0);
        second.AlreadyExisting.ShouldBe(apartments - 1);
        var charges = await ChargesAsync(period);
        charges.Count.ShouldBe(apartments - 1);
        charges.ShouldNotContain(c => c.ApartmentId == frozen.Id);
        charges.ShouldAllBe(c => c.Type == ChargeType.Maintenance);
        (await factory.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "ChargeCreated" && e.ChargeId != null, Ct)))
            .ShouldBeGreaterThanOrEqualTo(apartments - 1);

        var unfreeze = await founder.PostAsJsonAsync($"/api/admin/apartments/{frozen.Id}/unfreeze", new FreezeApartmentRequest("Nuevo inquilino"), Ct);
        await unfreeze.ShouldBeAsync(HttpStatusCode.NoContent);
        (await GenerateAsync(founder, 203002)).SkippedFrozen.ShouldBe(0);
    }

    [Fact]
    public async Task Fee_changes_apply_from_their_month_and_special_fees_win()
    {
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        var special = await founder.ApartmentAsync("H-101");
        var from = Math.Max(NextPeriod, CondoProfile.Current.Billing.TrackingStartPeriod) + 100; // a year later
        var before = Domain.Common.Period.FromValue(from).Previous().Value;
        await (await founder.PostAsJsonAsync("/api/admin/fees/global", new ChangeGlobalFeeRequest(Fee + 20, from, "Nuevo contrato"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);
        await (await founder.PostAsJsonAsync($"/api/admin/fees/apartments/{special.Id}", new ChangeApartmentFeeRequest(90m, from, "Local"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);

        await GenerateAsync(founder, before);
        await GenerateAsync(founder, from);

        (await ChargesAsync(before)).ShouldAllBe(c => c.Amount == Fee);
        var after = await ChargesAsync(from);
        after.Single(c => c.ApartmentId == special.Id).Amount.ShouldBe(90m);
        after.Where(c => c.ApartmentId != special.Id).ShouldAllBe(c => c.Amount == Fee + 20);
    }

    [Fact]
    public async Task Credit_pays_the_next_generated_month()
    {
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        var (resident, apartmentId, _) = await NewApartmentWithResidentAsync(founder, "gen-credit@test.local");
        await founder.ApprovedAsync(await resident.SubmitProofAsync(1000m, Guid.NewGuid().ToString("N")[..10]));
        (await resident.StatementAsync(factory, apartmentId)).Credit.ShouldBe(1000m);

        await GenerateAsync(founder, 203101);

        var statement = await resident.StatementAsync(factory, apartmentId);
        var charge = statement.Charge(203101);
        charge.Status.ShouldBe(DebtStatus.Paid);
        statement.Credit.ShouldBe(1000m - charge.Amount);
    }

    [Fact]
    public async Task Only_the_founder_generates_charges()
    {
        var admin = factory.CreateClient(await factory.RegisterAsync(AccountRole.Admin, "gen-admin@test.local", "F-502"));

        var response = await admin.PostAsJsonAsync("/api/admin/charges/generate", new GenerateChargesRequest(203201), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Opening_debt_is_imported_all_or_nothing_and_paid_before_new_months()
    {
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        var (resident, apartmentId, code) = await NewApartmentWithResidentAsync(founder, "gen-open@test.local");

        var bad = await founder.PostAsJsonAsync("/api/admin/charges/opening",
            new ImportOpeningDebtRequest($"apartmentCode,period,amount\n{code},2026-08,150\nZ-999,202609,150\n{code},202613,10"), Ct);
        var badResult = (await bad.Content.ReadFromJsonAsync<ImportOpeningDebtResult>(Ct))!;
        badResult.Created.ShouldBe(0);
        badResult.Errors.ShouldBe(["3:apartment", "4:period"]);

        var ok = await founder.PostAsJsonAsync("/api/admin/charges/opening",
            new ImportOpeningDebtRequest($"{code},2026-08,150\n{code.ToLowerInvariant()};202609;150.50"), Ct);
        (await ok.Content.ReadFromJsonAsync<ImportOpeningDebtResult>(Ct))!.Created.ShouldBe(2);
        var again = await founder.PostAsJsonAsync("/api/admin/charges/opening", new ImportOpeningDebtRequest($"{code},202608,150"), Ct);
        (await again.Content.ReadFromJsonAsync<ImportOpeningDebtResult>(Ct))!.Errors.ShouldBe(["1:duplicate"]);

        await GenerateAsync(founder, 203301);
        await founder.ApprovedAsync(await resident.SubmitProofAsync(300.50m, Guid.NewGuid().ToString("N")[..10]));

        var statement = await resident.StatementAsync(factory, apartmentId);
        statement.Charges.Where(c => c.Type == nameof(ChargeType.Opening)).ShouldAllBe(c => c.Status == DebtStatus.Paid);
        statement.Charge(203301).Status.ShouldBe(DebtStatus.Pending);
        statement.OverdueDebt.ShouldBe(0m);
    }

    [Fact]
    public async Task A_bad_period_is_rejected()
    {
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());

        var response = await founder.PostAsJsonAsync("/api/admin/charges/generate", new GenerateChargesRequest(202613), Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidPeriod);
    }
}
