using Microsoft.AspNetCore.Components.Authorization;

namespace Mantenio.Client.Auth;

/// <summary>Bridges <see cref="AuthSession"/> to Blazor's &lt;AuthorizeView&gt; and [Authorize].</summary>
public sealed class MantenioAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly AuthSession _session;

    public MantenioAuthenticationStateProvider(AuthSession session)
    {
        _session = session;
        _session.Changed += OnChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await _session.InitializeAsync();
        return new AuthenticationState(_session.User);
    }

    public void Dispose() => _session.Changed -= OnChanged;

    private void OnChanged() => NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_session.User)));
}
