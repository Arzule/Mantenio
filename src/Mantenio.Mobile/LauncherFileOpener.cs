using Mantenio.UI.Platform;

namespace Mantenio.Mobile;

internal sealed class LauncherFileOpener : IFileOpener
{
    public Task OpenAsync(Uri url) => Launcher.Default.OpenAsync(url);

    public async Task SaveAsync(string fileName, string contentType, byte[] content)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, Path.GetFileName(fileName));
        await File.WriteAllBytesAsync(path, content);
        await Launcher.Default.OpenAsync(new OpenFileRequest(fileName, new ReadOnlyFile(path, contentType)));
    }
}
