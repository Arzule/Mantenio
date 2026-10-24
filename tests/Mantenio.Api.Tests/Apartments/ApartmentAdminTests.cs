using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Shouldly;

namespace Mantenio.Api.Tests.Apartments;

public sealed class ApartmentAdminTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admins_list_every_apartment_with_its_residents()
    {
        await factory.RegisterAsync(AccountRole.Resident, "list1@test.local", "B-101");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());

        var all = await admin.GetFromJsonAsync<List<ApartmentAdminItem>>("/api/admin/apartments", Ct);

        all!.Count.ShouldBeGreaterThanOrEqualTo(180);
        all.Single(a => a.Code == "B-101").ResidentCount.ShouldBe(1);
        (await admin.GetFromJsonAsync<List<ApartmentAdminItem>>("/api/admin/apartments?search=b101", Ct))!
            .ShouldHaveSingleItem().Code.ShouldBe("B-101");
    }

    [Fact]
    public async Task Freezing_keeps_a_history_with_reasons()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var apartment = await admin.ApartmentAsync("C-101");

        await (await admin.PostAsJsonAsync($"/api/admin/apartments/{apartment.Id}/freeze", new FreezeApartmentRequest("Proceso judicial"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);
        var twice = await admin.PostAsJsonAsync($"/api/admin/apartments/{apartment.Id}/freeze", new FreezeApartmentRequest("Otra vez"), Ct);
        await twice.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ApartmentAlreadyFrozen);

        (await admin.GetFromJsonAsync<List<ApartmentAdminItem>>("/api/admin/apartments?frozen=true", Ct))!.ShouldContain(a => a.Code == "C-101");

        await (await admin.PostAsJsonAsync($"/api/admin/apartments/{apartment.Id}/unfreeze", new FreezeApartmentRequest("Acuerdo de pago"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);
        var notFrozen = await admin.PostAsJsonAsync($"/api/admin/apartments/{apartment.Id}/unfreeze", new FreezeApartmentRequest("x x"), Ct);
        await notFrozen.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ApartmentNotFrozen);

        var history = await admin.GetFromJsonAsync<List<ApartmentFreezeItem>>($"/api/admin/apartments/{apartment.Id}/freezes", Ct);
        var freeze = history!.ShouldHaveSingleItem();
        freeze.Reason.ShouldBe("Proceso judicial");
        freeze.EndReason.ShouldBe("Acuerdo de pago");
        freeze.StartedByName.ShouldBe("Fiorella Fundadora");
    }

    [Fact]
    public async Task Csv_import_previews_then_adds_only_what_is_new()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        const string csv = "tower,number\nA,101\nZ,101\nZ,102\n";

        var preview = await (await admin.PostAsJsonAsync("/api/admin/apartments/import/preview", new ImportApartmentsRequest(csv), Ct))
            .Content.ReadFromJsonAsync<ImportApartmentsPreview>(Ct);
        preview!.NewTowers.ShouldBe(["Z"]);
        preview.NewApartments.ShouldBe(["Z-101", "Z-102"]);
        preview.AlreadyExisting.ShouldBe(1);

        var result = await (await admin.PostAsJsonAsync("/api/admin/apartments/import", new ImportApartmentsRequest(csv), Ct))
            .Content.ReadFromJsonAsync<ImportApartmentsResult>(Ct);
        result!.ApartmentsAdded.ShouldBe(2);
        result.TowersAdded.ShouldBe(1);

        var again = await (await admin.PostAsJsonAsync("/api/admin/apartments/import", new ImportApartmentsRequest(csv), Ct))
            .Content.ReadFromJsonAsync<ImportApartmentsResult>(Ct);
        again!.ApartmentsAdded.ShouldBe(0);

        var bad = await (await admin.PostAsJsonAsync("/api/admin/apartments/import/preview", new ImportApartmentsRequest("A;101"), Ct))
            .Content.ReadFromJsonAsync<ImportApartmentsPreview>(Ct);
        bad!.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Only_unused_apartments_can_be_deleted()
    {
        await factory.RegisterAsync(AccountRole.Resident, "inuse@test.local", "D-101");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var towers = await admin.GetFromJsonAsync<List<TowerItem>>("/api/admin/towers", Ct);
        var created = await (await admin.PostAsJsonAsync("/api/admin/apartments", new CreateApartmentRequest(towers![0].Id, "999"), Ct))
            .Content.ReadFromJsonAsync<ApartmentAdminItem>(Ct);

        var duplicate = await admin.PostAsJsonAsync("/api/admin/apartments", new CreateApartmentRequest(towers[0].Id, "999"), Ct);
        await duplicate.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ApartmentExists);

        (await admin.DeleteAsync(new Uri($"/api/admin/apartments/{created!.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var inUse = await admin.DeleteAsync(new Uri($"/api/admin/apartments/{(await admin.ApartmentAsync("D-101")).Id}", UriKind.Relative), Ct);
        await inUse.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ApartmentInUse);
    }

    [Fact]
    public async Task Residents_cannot_manage_apartments()
    {
        var resident = await factory.RegisterAsync(AccountRole.Resident, "nope@test.local", "D-102");
        using var client = factory.CreateClient(resident);

        (await client.GetAsync(new Uri("/api/admin/apartments", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
