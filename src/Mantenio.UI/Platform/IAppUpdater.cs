namespace Mantenio.UI.Platform;

/// <summary>
/// Updating the installed app. Android knows its version code and installs a downloaded APK (Android asks the person
/// to confirm); the web always runs the latest version, so it has nothing to update.
/// </summary>
public interface IAppUpdater
{
    /// <summary>The installed version code, or null where updates do not apply (web).</summary>
    int? InstalledVersionCode { get; }

    /// <summary>Downloads the APK and opens the system installer. Progress goes from 0 to 1.</summary>
    Task InstallAsync(Uri apk, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Platforms that update by themselves (web).</summary>
public sealed class NoAppUpdater : IAppUpdater
{
    public int? InstalledVersionCode => null;

    public Task InstallAsync(Uri apk, IProgress<double>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
}
