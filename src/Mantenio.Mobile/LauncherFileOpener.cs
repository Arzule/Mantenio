using Mantenio.UI.Platform;

namespace Mantenio.Mobile;

internal sealed class LauncherFileOpener : IFileOpener
{
    public Task OpenAsync(Uri url) => Launcher.Default.OpenAsync(url);

    public async Task SaveAsync(string fileName, string contentType, byte[] content)
    {
        var path = await CacheAsync(fileName, content);
        await Launcher.Default.OpenAsync(new OpenFileRequest(fileName, new ReadOnlyFile(path, contentType)));
    }

    public async Task ShareToWhatsAppAsync(string fileName, string contentType, byte[] content, string message)
    {
        var path = await CacheAsync(fileName, content);
#if ANDROID
        if (WhatsAppShare.TryOpen(path, contentType, message))
        {
            return;
        }
#endif
        // No WhatsApp: the system share sheet (any messaging app).
        await Share.Default.RequestAsync(new ShareFileRequest { Title = message, File = new ShareFile(path, contentType) });
    }

    private static async Task<string> CacheAsync(string fileName, byte[] content)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, Path.GetFileName(fileName));
        await File.WriteAllBytesAsync(path, content);
        return path;
    }
}
