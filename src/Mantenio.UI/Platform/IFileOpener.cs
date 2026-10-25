namespace Mantenio.UI.Platform;

/// <summary>Opens private files: stored ones from a short-lived link, generated ones (reports) from their bytes.</summary>
public interface IFileOpener
{
    /// <summary>Web: a new browser tab. Android: the system viewer or browser.</summary>
    Task OpenAsync(Uri url);

    /// <summary>Web: downloads the file. Android: saves it to the cache and opens it with the system viewer.</summary>
    Task SaveAsync(string fileName, string contentType, byte[] content);

    /// <summary>
    /// Opens WhatsApp with the file attached and <paramref name="message"/> as its text, so the admin picks the
    /// resident and sends it. Android: WhatsApp directly (or the share sheet if it is not installed). Web: the
    /// browser's share sheet when it can share files, otherwise the file is downloaded and WhatsApp Web opens.
    /// </summary>
    Task ShareToWhatsAppAsync(string fileName, string contentType, byte[] content, string message);
}
