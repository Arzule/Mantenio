using SkiaSharp;

namespace Mantenio.Api.Infrastructure.Storage;

/// <summary>
/// Validates and re-encodes an uploaded transfer screenshot: it must really be an image, it is turned upright
/// (EXIF orientation), shrunk to at most <see cref="MaxSide"/> px and saved as JPEG, which also drops all
/// metadata (EXIF, GPS). Anything that does not decode as an image is rejected.
/// </summary>
public static class ProofImage
{
    public const int MaxUploadBytes = 10 * 1024 * 1024;
    public const int MaxSide = 1600;
    private const int JpegQuality = 80;

    /// <summary>Returns the JPEG bytes, or null when the data is not a supported image.</summary>
    public static byte[]? Normalize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            return null;
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null || decoded.Width == 0 || decoded.Height == 0)
        {
            return null;
        }

        using var upright = ApplyOrientation(decoded, codec.EncodedOrigin);
        var scale = Math.Min(1f, (float)MaxSide / Math.Max(upright.Width, upright.Height));
        var info = new SKImageInfo(Math.Max(1, (int)(upright.Width * scale)), Math.Max(1, (int)(upright.Height * scale)));
        using var resized = upright.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? upright.Copy();
        using var image = SKImage.FromBitmap(resized);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return jpeg.ToArray();
    }

    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return bitmap.Copy();
        }

        var rotate = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
        var result = rotate ? new SKBitmap(bitmap.Height, bitmap.Width) : new SKBitmap(bitmap.Width, bitmap.Height);
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, bitmap.Width / 2f, bitmap.Height / 2f);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(result.Width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, result.Height);
                canvas.RotateDegrees(270);
                break;
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1, bitmap.Width / 2f, 0);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1, 0, bitmap.Height / 2f);
                break;
            default:
                break;
        }

        canvas.DrawBitmap(bitmap, 0, 0);
        return result;
    }
}
