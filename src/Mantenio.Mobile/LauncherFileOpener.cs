using Mantenio.UI.Platform;

namespace Mantenio.Mobile;

internal sealed class LauncherFileOpener : IFileOpener
{
    public Task OpenAsync(Uri url) => Launcher.Default.OpenAsync(url);
}
