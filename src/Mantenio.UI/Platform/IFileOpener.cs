namespace Mantenio.UI.Platform;

/// <summary>Opens private files: stored ones from a short-lived link, generated ones (reports) from their bytes.</summary>
public interface IFileOpener
{
    /// <summary>Web: a new browser tab. Android: the system viewer or browser.</summary>
    Task OpenAsync(Uri url);

    /// <summary>Web: downloads the file. Android: saves it to the cache and opens it with the system viewer.</summary>
    Task SaveAsync(string fileName, string contentType, byte[] content);
}
