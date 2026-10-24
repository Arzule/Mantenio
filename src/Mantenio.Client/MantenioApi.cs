using System.Net.Http.Json;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Invitations;
using Mantenio.Shared.Users;

namespace Mantenio.Client;

public static class MantenioHttpClients
{
    /// <summary>No token: sign-in, registration, password reset and the apartment picker.</summary>
    public const string Public = "Mantenio.Public";

    /// <summary>Bearer token with automatic renewal.</summary>
    public const string Authenticated = "Mantenio.Authenticated";
}

/// <summary>Calls that work without being signed in.</summary>
public sealed class PublicApi(IHttpClientFactory httpClientFactory)
{
    public Task<SignInResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/login", request, cancellationToken);

    public Task<SignInResponse> VerifyTwoFactorAsync(VerifyCodeRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/two-factor", request, cancellationToken);

    public Task<SignInResponse> ResendTwoFactorAsync(string challengeToken, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/two-factor/resend", new ResendCodeRequest(challengeToken), cancellationToken);

    public Task<SignInResponse> ConfirmEmailAsync(VerifyCodeRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/email/confirm", request, cancellationToken);

    public Task<SignInResponse> ResendEmailConfirmationAsync(string challengeToken, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/email/resend", new ResendCodeRequest(challengeToken), cancellationToken);

    public Task<CheckInvitationResponse> CheckInvitationAsync(string code, CancellationToken cancellationToken = default) =>
        PostAsync<CheckInvitationResponse>("/api/invitations/check", new CheckInvitationRequest(code), cancellationToken);

    public Task<SignInResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/register", request, cancellationToken);

    public Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) =>
        PostAsync("/api/auth/password/forgot", new ForgotPasswordRequest(email), cancellationToken);

    public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("/api/auth/password/reset", request, cancellationToken);

    public async Task<IReadOnlyList<ApartmentOption>> SearchApartmentsAsync(string? search, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
        var url = $"/api/apartments/options?search={Uri.EscapeDataString(search ?? "")}";
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), cancellationToken).SendSafeAsync();
        return await response.ReadAsync<List<ApartmentOption>>(cancellationToken);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken).SendSafeAsync();
        return await response.ReadAsync<T>(cancellationToken);
    }

    private async Task PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken).SendSafeAsync();
        await response.EnsureSuccessAsync(cancellationToken);
    }
}

/// <summary>Calls for a signed-in user. Admin-only calls answer 403 to residents.</summary>
public sealed class MantenioApi(IHttpClientFactory httpClientFactory)
{
    public Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CurrentUserResponse>("/api/account/me", cancellationToken);

    public Task<AuthTokens> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AuthTokens>(HttpMethod.Post, "/api/account/password", request, cancellationToken);

    public Task<CreateInvitationResponse> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreateInvitationResponse>(HttpMethod.Post, "/api/admin/invitations", request, cancellationToken);

    public Task<PagedResult<UserSummary>> ListUsersAsync(
        string? search, bool includeInactive, int page, int pageSize, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<UserSummary>>(
            $"/api/admin/users?search={Uri.EscapeDataString(search ?? "")}&includeInactive={(includeInactive ? "true" : "false")}&page={page}&pageSize={pageSize}",
            cancellationToken);

    public Task DeactivateUserAsync(Guid userId, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/admin/users/{userId}/deactivate", new ChangeUserStatusRequest(reason), cancellationToken);

    public Task ReactivateUserAsync(Guid userId, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/admin/users/{userId}/reactivate", new ChangeUserStatusRequest(reason), cancellationToken);

    public Task ChangeUserApartmentAsync(Guid userId, Guid? apartmentId, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/apartment", new ChangeUserApartmentRequest(apartmentId, reason), cancellationToken);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Authenticated);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).SendSafeAsync();
        return await response.ReadAsync<T>(cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, cancellationToken);
        return await response.ReadAsync<T>(cancellationToken);
    }

    private async Task SendAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, cancellationToken);
        await response.EnsureSuccessAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Authenticated);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        return await client.SendAsync(request, cancellationToken).SendSafeAsync();
    }
}
