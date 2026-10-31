using System.Globalization;
using System.Text;
using QRCoder;

namespace Mantenio.UI.Components;

/// <summary>Draws a QR code as an inline SVG (one path, black on white), so it renders the same in every browser.</summary>
public static class QrSvg
{
    public static string Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (matrix[y][x])
                {
                    path.Append(CultureInfo.InvariantCulture, $"M{x} {y}h1v1h-1z");
                }
            }
        }

        return $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" shape-rendering="crispEdges"><rect width="{size}" height="{size}" fill="#fff"/><path d="{path}" fill="#000"/></svg>""";
    }
}
