using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Invitations;
using Shouldly;

namespace Mantenio.Api.Tests.Infrastructure;

/// <summary>The common end-to-end flows (sign in, invite, register) written once for every test class.</summary>
public static class TestFlows
{
    public const string ResidentPassword = "Vecino2026";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SignInResponse> LoginAsync(this HttpClient client, string email, string password, bool keepSignedIn = false)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password, keepSignedIn), Ct);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SignInResponse>(Ct))!;
    }

    /// <summary>Password + emailed code. Returns the tokens.</summary>
    public static async Task<AuthTokens> SignInAdminAsync(this ApiFactory factory, string email, string password, bool keepSignedIn = false)
    {
        using var client = factory.CreateClient();
        var login = await client.LoginAsync(email, password, keepSignedIn);
        login.Step.ShouldBe(SignInStep.TwoFactorRequired);
        return await client.VerifyCodeAsync("/api/auth/two-factor", login.ChallengeToken!, factory.Emails.LastCodeFor(email));
    }

    public static Task<AuthTokens> SignInFounderAsync(this ApiFactory factory, bool keepSignedIn = false) =>
        factory.SignInAdminAsync(ApiFactory.FounderEmail, ApiFactory.FounderPassword, keepSignedIn);

    public static async Task<AuthTokens> VerifyCodeAsync(this HttpClient client, string path, string challengeToken, string code)
    {
        var response = await client.PostAsJsonAsync(path, new VerifyCodeRequest(challengeToken, code), Ct);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<SignInResponse>(Ct))!;
        result.Step.ShouldBe(SignInStep.SignedIn);
        return result.Tokens!;
    }

    public static HttpClient CreateClient(this ApiFactory factory, AuthTokens tokens)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public static async Task<ApartmentOption> ApartmentAsync(this HttpClient client, string code)
    {
        var options = await client.GetFromJsonAsync<List<ApartmentOption>>($"/api/apartments/options?search={code}", Ct);
        return options!.Single(o => o.Code == code);
    }

    public static async Task<string> CreateInvitationAsync(this HttpClient adminClient, AccountRole role, Guid? apartmentId = null)
    {
        var response = await adminClient.PostAsJsonAsync("/api/admin/invitations", new CreateInvitationRequest(role, apartmentId), Ct);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<CreateInvitationResponse>(Ct))!.Code;
    }

    /// <summary>Invitation from the founder → registration → email confirmation. Returns the new user's tokens.</summary>
    public static async Task<AuthTokens> RegisterAsync(
        this ApiFactory factory, AccountRole role, string email, string? apartmentCode, bool keepSignedIn = false)
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var code = await admin.CreateInvitationAsync(role);

        using var client = factory.CreateClient();
        Guid? apartmentId = apartmentCode is null ? null : (await client.ApartmentAsync(apartmentCode)).Id;
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(code, "Nombre", "Apellido", email, ResidentPassword, apartmentId, keepSignedIn),
            Ct);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        var registration = (await response.Content.ReadFromJsonAsync<SignInResponse>(Ct))!;
        registration.Step.ShouldBe(SignInStep.EmailConfirmationRequired);

        return await client.VerifyCodeAsync("/api/auth/email/confirm", registration.ChallengeToken!, factory.Emails.LastCodeFor(email));
    }

    /// <summary>Asserts the status code, showing the response body when it does not match.</summary>
    /// <summary>GET as JSON, failing with the server-side error when the endpoint answers 500.</summary>
    public static async Task<T> GetJsonAsync<T>(this HttpClient client, ApiFactory factory, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, factory.ServerErrors));
        }

        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    public static async Task ShouldBeAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(expected, body);
        }
    }

    /// <summary>Asserts a ProblemDetails response with the given status and error code.</summary>
    public static async Task ShouldBeProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe(code, body);
    }
}
