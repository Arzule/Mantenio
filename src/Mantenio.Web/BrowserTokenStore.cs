using System.Text.Json;
using Mantenio.Client.Auth;
using Microsoft.JSInterop;

namespace Mantenio.Web;

/// <summary>
/// "Keep me signed in" → localStorage (survives closing the browser); otherwise sessionStorage (this tab only).
/// Accepted pilot risk: an XSS could read it, so the API keeps refresh tokens short-lived and rotating.
/// </summary>
internal sealed class BrowserTokenStore(IJSRuntime js) : ITokenStore
{
    private const string Key = "mantenio.session";

    public async Task<StoredSession?> LoadAsync()
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", Key)
            ?? await js.InvokeAsync<string?>("sessionStorage.getItem", Key);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredSession>(json);
        }
        catch (JsonException)
        {
            await ClearAsync();
            return null;
        }
    }

    public async Task SaveAsync(StoredSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        await ClearAsync();
        var storage = session.KeepSignedIn ? "localStorage" : "sessionStorage";
        await js.InvokeVoidAsync($"{storage}.setItem", Key, JsonSerializer.Serialize(session));
    }

    public async Task ClearAsync()
    {
        await js.InvokeVoidAsync("localStorage.removeItem", Key);
        await js.InvokeVoidAsync("sessionStorage.removeItem", Key);
    }
}
