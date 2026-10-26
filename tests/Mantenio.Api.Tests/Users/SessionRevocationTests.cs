using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Shouldly;

namespace Mantenio.Api.Tests.Users;

/// <summary>A revoked session stops working at once, not when its 15-minute access token expires.</summary>
public sealed class SessionRevocationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changing_the_password_signs_out_the_other_phones_immediately()
    {
        const string email = "family@test.local";
        var firstPhone = await factory.RegisterAsync(AccountRole.Resident, email, "B-201");
        using var anonymous = factory.CreateClient();
        var secondLogin = await anonymous.LoginAsync(email, TestFlows.ResidentPassword);
        using var secondPhone = factory.CreateClient(secondLogin.Tokens!);
        using var first = factory.CreateClient(firstPhone);

        (await secondPhone.GetAsync(new Uri("/api/account/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var change = await first.PostAsJsonAsync("/api/account/password",
            new ChangePasswordRequest(TestFlows.ResidentPassword, "Familia2027", false), Ct);
        await change.ShouldBeAsync(HttpStatusCode.OK);
        var renewed = (await change.Content.ReadFromJsonAsync<AuthTokens>(Ct))!;

        (await secondPhone.GetAsync(new Uri("/api/account/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await first.GetAsync(new Uri("/api/account/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var current = factory.CreateClient(renewed);
        (await current.GetAsync(new Uri("/api/account/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logging_out_ends_that_session_only()
    {
        const string email = "two-devices@test.local";
        var web = await factory.RegisterAsync(AccountRole.Resident, email, "B-202");
        using var anonymous = factory.CreateClient();
        var phone = (await anonymous.LoginAsync(email, TestFlows.ResidentPassword)).Tokens!;

        await (await anonymous.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(web.RefreshToken), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);

        using var webClient = factory.CreateClient(web);
        using var phoneClient = factory.CreateClient(phone);
        (await webClient.GetAsync(new Uri("/api/account/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await phoneClient.GetAsync(new Uri("/api/account/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
