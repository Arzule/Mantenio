using Mantenio.UI.Platform;

namespace Mantenio.Mobile;

/// <summary>System camera and gallery; photos are shrunk on the phone (the server re-encodes them anyway).</summary>
internal sealed class MediaImagePicker : IImagePicker
{
    private const int MaxSide = 1600;

    public bool IsSupported => true;

    public async Task<PickedImage?> CaptureAsync()
    {
        if (!MediaPicker.Default.IsCaptureSupported)
        {
            return null;
        }

        return await ReadAsync(await MainThread.InvokeOnMainThreadAsync(() => MediaPicker.Default.CapturePhotoAsync(Options())));
    }

    public async Task<PickedImage?> PickAsync()
    {
        var results = await MainThread.InvokeOnMainThreadAsync(() => MediaPicker.Default.PickPhotosAsync(Options()));
        return await ReadAsync(results?.FirstOrDefault());
    }

    private static MediaPickerOptions Options() => new()
    {
        MaximumWidth = MaxSide,
        MaximumHeight = MaxSide,
        CompressionQuality = 85,
        SelectionLimit = 1,
    };

    private static async Task<PickedImage?> ReadAsync(FileResult? file)
    {
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return new PickedImage(buffer.ToArray(), string.IsNullOrEmpty(file.ContentType) ? "image/jpeg" : file.ContentType, file.FileName);
    }
}
