using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Shouldly;

namespace Mantenio.Api.Tests.Auth;

public sealed class SessionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refreshing_rotates_the_refresh_token()
    {
        var tokens = await factory.SignInFounderAsync();
        using var client = factory.CreateClient();

        var refreshed = await RefreshAsync(client, tokens.RefreshToken);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var next = (await refreshed.Content.ReadFromJsonAsync<AuthTokens>(Ct))!;
        next.RefreshToken.ShouldNotBe(tokens.RefreshToken);
        using var authed = factory.CreateClient(next);
        (await authed.GetAsync(new Uri("/api/account/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_session()
    {
        var tokens = await factory.SignInFounderAsync();
        using var client = factory.CreateClient();
        var next = (await (await RefreshAsync(client, tokens.RefreshToken)).Content.ReadFromJsonAsync<AuthTokens>(Ct))!;

        var replay = await RefreshAsync(client, tokens.RefreshToken);
        await replay.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);

        // The legitimate holder of the newer token is signed out too: the stolen family is dead.
        var legit = await RefreshAsync(client, next.RefreshToken);
        await legit.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var tokens = await factory.SignInFounderAsync();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(tokens.RefreshToken), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var refresh = await RefreshAsync(client, tokens.RefreshToken);
        await refresh.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);
    }

    [Fact]
    public async Task Other_sessions_survive_a_logout()
    {
        var phone = await factory.SignInFounderAsync();
        var laptop = await factory.SignInFounderAsync();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(laptop.RefreshToken), Ct);

        (await RefreshAsync(client, phone.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Password_reset_works_with_the_emailed_code_and_signs_out_everywhere()
    {
        var oldSession = await factory.RegisterAsync(AccountRole.Resident, "carla@test.local", "B-201");
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/auth/password/forgot", new ForgotPasswordRequest("carla@test.local"), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = factory.Emails.LastCodeFor("carla@test.local");

        var wrong = await client.PostAsJsonAsync("/api/auth/password/reset", new ResetPasswordRequest("carla@test.local", "000000", "Nueva2026"), Ct);
        await wrong.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidCode);

        var weak = await client.PostAsJsonAsync("/api/auth/password/reset", new ResetPasswordRequest("carla@test.local", code, "sinnumeros"), Ct);
        await weak.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.WeakPassword);

        var reset = await client.PostAsJsonAsync("/api/auth/password/reset", new ResetPasswordRequest("carla@test.local", code, "Nueva2026"), Ct);
        await reset.ShouldBeAsync(HttpStatusCode.NoContent);

        (await client.LoginAsync("carla@test.local", "Nueva2026")).Step.ShouldBe(SignInStep.SignedIn);
        var stale = await RefreshAsync(client, oldSession.RefreshToken);
        await stale.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);
    }

    [Fact]
    public async Task Forgot_password_does_not_reveal_whether_an_email_exists()
    {
        using var client = factory.CreateClient();
        var before = factory.Emails.Messages.Count;

        var response = await client.PostAsJsonAsync("/api/auth/password/forgot", new ForgotPasswordRequest("ghost@test.local"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Emails.Messages.Count.ShouldBe(before);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken), Ct);
}
