using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Shouldly;

namespace Mantenio.Api.Tests.Auth;

public sealed class LoginTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Residents_sign_in_with_email_and_password_only()
    {
        await factory.RegisterAsync(AccountRole.Resident, "ana@test.local", "A-101");
        using var client = factory.CreateClient();

        var login = await client.LoginAsync("ana@test.local", TestFlows.ResidentPassword);

        login.Step.ShouldBe(SignInStep.SignedIn);
        using var authed = factory.CreateClient(login.Tokens!);
        var me = await authed.GetFromJsonAsync<CurrentUserResponse>("/api/account/me", Ct);
        me!.Role.ShouldBe(AccountRole.Resident);
        me.ApartmentCode.ShouldBe("A-101");
    }

    [Fact]
    public async Task Admins_get_no_tokens_until_they_enter_the_emailed_code()
    {
        using var client = factory.CreateClient();

        var login = await client.LoginAsync(ApiFactory.FounderEmail, ApiFactory.FounderPassword);

        login.Step.ShouldBe(SignInStep.TwoFactorRequired);
        login.Tokens.ShouldBeNull();
        login.MaskedEmail.ShouldBe("f•••••r@test.local");

        var wrong = await client.PostAsJsonAsync("/api/auth/two-factor", new VerifyCodeRequest(login.ChallengeToken!, "000000"), Ct);
        await wrong.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidCode);

        var tokens = await client.VerifyCodeAsync("/api/auth/two-factor", login.ChallengeToken!, factory.Emails.LastCodeFor(ApiFactory.FounderEmail));
        tokens.AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_challenge_token_is_not_an_access_token()
    {
        using var client = factory.CreateClient();
        var login = await client.LoginAsync(ApiFactory.FounderEmail, ApiFactory.FounderPassword);
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.ChallengeToken);

        var response = await client.GetAsync(new Uri("/api/account/me", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(true, 30 * 24)]
    [InlineData(false, 12)]
    public async Task Keep_signed_in_sets_how_long_an_admin_session_lasts(bool keepSignedIn, int hours)
    {
        var tokens = await factory.SignInFounderAsync(keepSignedIn);

        (tokens.RefreshTokenExpiresAt - factory.Time.GetUtcNow()).ShouldBe(TimeSpan.FromHours(hours), TimeSpan.FromMinutes(1));
        (tokens.AccessTokenExpiresAt - factory.Time.GetUtcNow()).ShouldBe(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Wrong_passwords_lock_the_account_after_five_attempts()
    {
        await factory.RegisterAsync(AccountRole.Resident, "bruno@test.local", "A-102");
        using var client = factory.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            var attempt = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("bruno@test.local", "nope12345", false), Ct);
            await attempt.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        }

        var fifth = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("bruno@test.local", "nope12345", false), Ct);
        await fifth.ShouldBeProblemAsync(HttpStatusCode.Locked, ErrorCodes.AccountLocked);

        var evenRight = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("bruno@test.local", TestFlows.ResidentPassword, false), Ct);
        await evenRight.ShouldBeProblemAsync(HttpStatusCode.Locked, ErrorCodes.AccountLocked);
    }

    [Fact]
    public async Task Unknown_emails_get_the_same_answer_as_wrong_passwords()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@test.local", "whatever1", false), Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task Invalid_requests_return_validation_failed()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "not-an-email", password = "" }, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }
}
