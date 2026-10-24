namespace Mantenio.UI.Platform;

/// <summary>Opens a private file (receipt PDF, payment screenshot) from its short-lived link.</summary>
public interface IFileOpener
{
    /// <summary>Web: a new browser tab. Android: the system viewer or browser.</summary>
    Task OpenAsync(Uri url);
}
