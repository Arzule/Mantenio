namespace Mantenio.Client.Auth;

public sealed record StoredSession(string RefreshToken, bool KeepSignedIn);

/// <summary>
/// Where the refresh token survives app restarts. Android: SecureStorage. Web: localStorage when
/// "keep me signed in" is checked, otherwise sessionStorage (gone when the browser closes).
/// The access token never leaves memory.
/// </summary>
public interface ITokenStore
{
    Task<StoredSession?> LoadAsync();

    Task SaveAsync(StoredSession session);

    Task ClearAsync();
}
