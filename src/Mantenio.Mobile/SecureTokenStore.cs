using System.Text.Json;
using Mantenio.Client.Auth;

namespace Mantenio.Mobile;

/// <summary>Android Keystore-backed storage for the refresh token.</summary>
internal sealed class SecureTokenStore : ITokenStore
{
    private const string Key = "mantenio.session";

    public async Task<StoredSession?> LoadAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(Key);
            return json is null ? null : JsonSerializer.Deserialize<StoredSession>(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Unreadable after a backup restore or a keystore reset: start signed out.
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }

    public Task SaveAsync(StoredSession session) =>
        SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(session));

    public Task ClearAsync()
    {
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }
}
