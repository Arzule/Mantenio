namespace Mantenio.UI.Platform;

/// <summary>
/// Push notifications for this device. Android registers its Firebase token with the API after sign-in and removes
/// it on sign-out; the web has no push (notices stay in the app).
/// </summary>
public interface IPushRegistration
{
    Task RegisterAsync();

    Task UnregisterAsync();
}

/// <summary>Platforms without push (web).</summary>
public sealed class NoPushRegistration : IPushRegistration
{
    public Task RegisterAsync() => Task.CompletedTask;

    public Task UnregisterAsync() => Task.CompletedTask;
}
