namespace Mantenio.UI.Platform;

public sealed record PickedImage(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Camera and gallery. Android uses the system pickers (the web view's file input cannot open the camera); on the web
/// <see cref="IsSupported"/> is false and pages use a file input (phone browsers offer the camera there).
/// </summary>
public interface IImagePicker
{
    bool IsSupported { get; }

    /// <summary>Null when the user cancels.</summary>
    Task<PickedImage?> CaptureAsync();

    /// <summary>Null when the user cancels.</summary>
    Task<PickedImage?> PickAsync();
}

/// <summary>Platforms that use a file input instead (web).</summary>
public sealed class NoImagePicker : IImagePicker
{
    public bool IsSupported => false;

    public Task<PickedImage?> CaptureAsync() => Task.FromResult<PickedImage?>(null);

    public Task<PickedImage?> PickAsync() => Task.FromResult<PickedImage?>(null);
}
