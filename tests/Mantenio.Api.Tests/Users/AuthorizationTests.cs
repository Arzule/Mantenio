using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Shouldly;

namespace Mantenio.Api.Tests.Users;

public sealed class AuthorizationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_endpoints_need_an_admin()
    {
        var resident = await factory.RegisterAsync(AccountRole.Resident, "res1@test.local", "G-101");
        using var anonymous = factory.CreateClient();
        using var residentClient = factory.CreateClient(resident);

        (await anonymous.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await residentClient.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await residentClient.PostAsJsonAsync("/api/admin/invitations", new { role = "Resident" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Residents_only_see_their_own_apartment()
    {
        var resident = await factory.RegisterAsync(AccountRole.Resident, "res2@test.local", "G-102");
        using var client = factory.CreateClient(resident);
        var own = await client.ApartmentAsync("G-102");
        var other = await client.ApartmentAsync("G-103");

        var ownResponse = await client.GetAsync(new Uri($"/api/apartments/{own.Id}", UriKind.Relative), Ct);
        await ownResponse.ShouldBeAsync(HttpStatusCode.OK);
        var details = await ownResponse.Content.ReadFromJsonAsync<ApartmentDetails>(Ct);
        details!.Residents.ShouldContain(r => r.FirstName == "Nombre");

        (await client.GetAsync(new Uri($"/api/apartments/{other.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        (await admin.GetAsync(new Uri($"/api/apartments/{other.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_apartment_picker_finds_codes_however_they_are_typed()
    {
        using var client = factory.CreateClient();

        foreach (var search in new[] { "h102", "H-102", "h 102" })
        {
            var options = await client.GetFromJsonAsync<List<ApartmentOption>>($"/api/apartments/options?search={Uri.EscapeDataString(search)}", Ct);
            options!.Select(o => o.Code).ShouldBe(["H-102"]);
        }

        var all = await client.GetFromJsonAsync<List<ApartmentOption>>("/api/apartments/options?limit=50", Ct);
        all!.Count.ShouldBe(50);
        all[0].Code.ShouldBe("A-101");
    }
}

/// <summary>The founder seeded by `seed-founder` has a temporary password that blocks everything else until changed.</summary>
public sealed class TemporaryPasswordTests(TemporaryPasswordTests.Factory factory) : IClassFixture<TemporaryPasswordTests.Factory>
{
    public sealed class Factory : ApiFactory
    {
        protected override bool FounderHasTemporaryPassword => true;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_founder_must_change_the_temporary_password_first()
    {
        var temporary = factory.FounderTemporaryPassword!;
        var tokens = await factory.SignInAdminAsync(ApiFactory.FounderEmail, temporary);
        using var client = factory.CreateClient(tokens);

        (await client.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var me = await client.GetFromJsonAsync<CurrentUserResponse>("/api/account/me", Ct);
        me!.MustChangePassword.ShouldBeTrue();

        var change = await client.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(temporary, "Presidente2026", false), Ct);
        await change.ShouldBeAsync(HttpStatusCode.OK);
        var fresh = (await change.Content.ReadFromJsonAsync<AuthTokens>(Ct))!;

        using var freshClient = factory.CreateClient(fresh);
        (await freshClient.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
