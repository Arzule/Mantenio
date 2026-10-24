using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Notifications;
using Mantenio.Shared.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.Settings;

public sealed class SettingsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int CurrentPeriod => factory.Services.GetRequiredService<CondoClock>().CurrentPeriod.Value;

    private int NextPeriod => factory.Services.GetRequiredService<CondoClock>().CurrentPeriod.Next().Value;

    [Fact]
    public async Task Settings_start_from_the_condo_profile()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());

        var settings = await admin.GetJsonAsync<CondoSettingsResponse>(factory, "/api/admin/settings");

        var billing = CondoProfile.Current.Billing;
        settings!.CurrentGlobalFee.ShouldBe(billing.MonthlyFee);
        settings.DueDay.ShouldBe(billing.DueDay);
        settings.DueMonthOffset.ShouldBe(billing.DueMonthOffset);
        settings.LateFeePercent.ShouldBe(0m);
        settings.CurrentPeriod.ShouldBe(CurrentPeriod);
    }

    [Fact]
    public async Task A_global_fee_change_is_scheduled_for_a_future_month_and_every_user_is_told()
    {
        var resident = await factory.RegisterAsync(AccountRole.Resident, "fee1@test.local", "A-201");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var period = Math.Max(NextPeriod + 1, CondoProfile.Current.Billing.TrackingStartPeriod + 1);

        var thisMonth = await admin.PostAsJsonAsync("/api/admin/fees/global", new ChangeGlobalFeeRequest(200m, CurrentPeriod, "Nuevo contrato"), Ct);
        await thisMonth.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidPeriod);

        var response = await admin.PostAsJsonAsync("/api/admin/fees/global", new ChangeGlobalFeeRequest(200m, period, "Nuevo contrato de vigilancia"), Ct);
        await response.ShouldBeAsync(HttpStatusCode.NoContent);

        var settings = await admin.GetJsonAsync<CondoSettingsResponse>(factory, "/api/admin/settings");
        settings!.ScheduledGlobalChanges.ShouldContain(c => c.NewAmount == 200m && c.EffectivePeriod == period && c.ChangedByName == "Fiorella Fundadora");

        using var residentClient = factory.CreateClient(resident);
        var mine = await residentClient.GetFromJsonAsync<MyFeeResponse>("/api/fees/mine", Ct);
        mine!.ApartmentCode.ShouldBe("A-201");
        mine.CurrentFee.ShouldBe(CondoProfile.Current.Billing.MonthlyFee);
        mine.NextFee.ShouldBe(200m);
        mine.NextFeePeriod.ShouldBe(period);

        var notices = await residentClient.GetFromJsonAsync<NotificationPage>("/api/notifications", Ct);
        var notice = notices!.Items.ShouldHaveSingleItem();
        notice.Kind.ShouldBe(NotificationKind.FeeChanged);
        notice.RequiresAcknowledgement.ShouldBeFalse();
        notice.Data.GetProperty("reason").GetString().ShouldBe("Nuevo contrato de vigilancia");
        (await factory.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "GlobalFeeChanged", Ct))).ShouldBeTrue();
    }

    [Fact]
    public async Task An_apartment_special_fee_only_affects_that_apartment()
    {
        var resident = await factory.RegisterAsync(AccountRole.Resident, "fee2@test.local", "A-301");
        var neighbor = await factory.RegisterAsync(AccountRole.Resident, "fee3@test.local", "A-302");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var apartment = await admin.ApartmentAsync("A-301");
        var period = Math.Max(NextPeriod, CondoProfile.Current.Billing.TrackingStartPeriod);

        var response = await admin.PostAsJsonAsync($"/api/admin/fees/apartments/{apartment.Id}",
            new ChangeApartmentFeeRequest(90m, period, "Local pequeño"), Ct);
        await response.ShouldBeAsync(HttpStatusCode.NoContent);

        using var residentClient = factory.CreateClient(resident);
        (await residentClient.GetFromJsonAsync<MyFeeResponse>("/api/fees/mine", Ct))!.NextFee.ShouldBe(90m);
        using var neighborClient = factory.CreateClient(neighbor);
        (await neighborClient.GetFromJsonAsync<MyFeeResponse>("/api/fees/mine", Ct))!.NextFee.ShouldNotBe(90m);
        (await neighborClient.GetFromJsonAsync<NotificationPage>("/api/notifications", Ct))!.Items
            .ShouldNotContain(n => n.Data.GetProperty("apartmentCode").GetString() == "A-301");

        var history = await admin.GetFromJsonAsync<List<FeeChangeItem>>($"/api/admin/fees/history?apartmentId={apartment.Id}", Ct);
        history!.ShouldHaveSingleItem().ApartmentCode.ShouldBe("A-301");
    }

    [Fact]
    public async Task Only_the_founder_changes_the_late_fee()
    {
        var otherAdmin = await factory.RegisterAsync(AccountRole.Admin, "admin.fee@test.local", null);
        _ = otherAdmin;
        using var admin = factory.CreateClient(await factory.SignInAdminAsync("admin.fee@test.local", TestFlows.ResidentPassword));
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());

        (await admin.PutAsJsonAsync("/api/admin/settings/late-fee", new ChangeLateFeeRequest(5m, "Acuerdo de asamblea"), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var response = await founder.PutAsJsonAsync("/api/admin/settings/late-fee", new ChangeLateFeeRequest(5m, "Acuerdo de asamblea"), Ct);
        await response.ShouldBeAsync(HttpStatusCode.NoContent);
        (await founder.GetJsonAsync<CondoSettingsResponse>(factory, "/api/admin/settings"))!.LateFeePercent.ShouldBe(5m);

        // Back to the default: the other tests of this class share the database and expect 0 %.
        await (await founder.PutAsJsonAsync("/api/admin/settings/late-fee", new ChangeLateFeeRequest(0m, "Fin de la prueba"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Admins_change_the_due_rule_with_a_reason()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());

        var response = await admin.PutAsJsonAsync("/api/admin/settings/due-rule", new ChangeDueRuleRequest(10, 0, "Acuerdo de junta"), Ct);
        await response.ShouldBeAsync(HttpStatusCode.NoContent);

        var settings = await admin.GetJsonAsync<CondoSettingsResponse>(factory, "/api/admin/settings");
        settings!.DueDay.ShouldBe(10);
        settings.DueMonthOffset.ShouldBe(0);
        (await factory.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "DueRuleChanged", Ct))).Reason.ShouldBe("Acuerdo de junta");

        await admin.PutAsJsonAsync("/api/admin/settings/due-rule",
            new ChangeDueRuleRequest(CondoProfile.Current.Billing.DueDay, CondoProfile.Current.Billing.DueMonthOffset, "Volver"), Ct);
    }
}
