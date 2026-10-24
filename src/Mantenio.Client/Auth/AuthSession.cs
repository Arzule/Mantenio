using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Mantenio.Shared.Auth;

namespace Mantenio.Client.Auth;

/// <summary>
/// The signed-in user of this app instance: keeps the access token in memory, renews it with the stored
/// refresh token (one refresh at a time) and raises <see cref="Changed"/> when the user signs in or out.
/// </summary>
public sealed class AuthSession(IHttpClientFactory httpClientFactory, ITokenStore store) : IDisposable
{
    /// <summary>Renew a bit before expiry so a request never leaves with a token about to die.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;
    private Task? _initialization;

    public ClaimsPrincipal User { get; private set; } = Anonymous;

    /// <summary>What the user chose at sign-in; reused when a password change starts a new session.</summary>
    public bool KeepSignedIn { get; private set; }

    public event Action? Changed;

    /// <summary>Restores the previous session (if any) once per app start.</summary>
    public Task InitializeAsync() => _initialization ??= RefreshAsync();

    public async Task SignInAsync(AuthTokens tokens, bool keepSignedIn)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        await store.SaveAsync(new StoredSession(tokens.RefreshToken, keepSignedIn));
        KeepSignedIn = keepSignedIn;
        Apply(tokens);
        _initialization = Task.CompletedTask;
    }

    /// <summary>A valid access token, renewed if needed; null when nobody is signed in.</summary>
    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        await InitializeAsync();
        if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - RenewBefore)
        {
            return _accessToken;
        }

        var stale = _accessToken;
        await _gate.WaitAsync();
        try
        {
            // Another caller may have refreshed while this one waited.
            if (_accessToken is not null && _accessToken != stale && DateTimeOffset.UtcNow < _accessTokenExpiresAt - RenewBefore)
            {
                return _accessToken;
            }

            await RefreshCoreAsync();
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    public async Task SignOutAsync()
    {
        var session = await store.LoadAsync();
        if (session is not null)
        {
            try
            {
                using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
                await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(session.RefreshToken));
            }
            catch (HttpRequestException)
            {
                // Offline: the server session expires on its own; locally we sign out anyway.
            }
        }

        await ClearAsync();
    }

    private async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshCoreAsync()
    {
        var session = await store.LoadAsync();
        if (session is null)
        {
            SetSignedOut();
            return;
        }

        HttpResponseMessage response;
        try
        {
            using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
            response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken));
        }
        catch (HttpRequestException)
        {
            // No connection: keep the stored session and try again on the next request.
            return;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await ClearAsync();
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var tokens = await response.Content.ReadFromJsonAsync<AuthTokens>();
            if (tokens is not null)
            {
                await store.SaveAsync(session with { RefreshToken = tokens.RefreshToken });
                KeepSignedIn = session.KeepSignedIn;
                Apply(tokens);
            }
        }
    }

    private async Task ClearAsync()
    {
        await store.ClearAsync();
        SetSignedOut();
    }

    private void SetSignedOut()
    {
        var wasSignedIn = _accessToken is not null;
        _accessToken = null;
        _accessTokenExpiresAt = default;
        User = Anonymous;
        if (wasSignedIn)
        {
            Changed?.Invoke();
        }
    }

    private void Apply(AuthTokens tokens)
    {
        _accessToken = tokens.AccessToken;
        _accessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        User = new ClaimsPrincipal(new ClaimsIdentity(
            ReadClaims(tokens.AccessToken), "jwt", MantenioClaims.Name, MantenioClaims.Role));
        Changed?.Invoke();
    }

    /// <summary>Reads the JWT payload. No signature check: the API validates the token on every call.</summary>
    private static List<Claim> ReadClaims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var claims = new List<Claim>();
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                claims.AddRange(property.Value.EnumerateArray().Select(v => new Claim(property.Name, v.ToString())));
            }
            else
            {
                claims.Add(new Claim(property.Name, property.Value.ToString()));
            }
        }

        return claims;
    }
}
