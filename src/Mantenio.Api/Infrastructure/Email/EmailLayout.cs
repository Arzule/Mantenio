using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Mantenio.Api.Resources;
using Mantenio.Shared.Condos;

namespace Mantenio.Api.Infrastructure.Email;

/// <summary>
/// HTML version of every email, built from its plain text: a header in the condo's colors with its name, the text
/// as paragraphs (a line with only a 6-digit code is shown large) and a footer. No images, so mail clients that
/// block them still show the branding.
/// </summary>
public static partial class EmailLayout
{
    public static string Html(string subject, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var profile = CondoProfile.Current;
        var theme = profile.Theme;
        var (appBar, background, surface, textColor) =
            (theme.AppBar ?? theme.Primary, theme.Background ?? "#F3F5F9", theme.Surface ?? "#FFFFFF", theme.TextPrimary ?? "#1F2937");
        var paragraphs = new StringBuilder();
        foreach (var block in text.Replace("\r", "", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = block.Trim();
            if (Code().IsMatch(trimmed))
            {
                paragraphs.Append(CultureInfo.InvariantCulture,
                    $"<p style=\"font-size:32px;letter-spacing:6px;font-weight:bold;color:{theme.Primary};margin:24px 0;text-align:center\">{trimmed}</p>");
            }
            else
            {
                paragraphs.Append(CultureInfo.InvariantCulture,
                    $"<p style=\"margin:0 0 14px\">{WebUtility.HtmlEncode(trimmed).Replace("\n", "<br>", StringComparison.Ordinal)}</p>");
            }
        }

        var footer = WebUtility.HtmlEncode(Format(profile.Culture, EmailTexts.Footer, profile.DisplayName));
        return $"""
            <!doctype html>
            <html lang="es"><head><meta charset="utf-8"><title>{WebUtility.HtmlEncode(subject)}</title></head>
            <body style="margin:0;padding:0;background:{background};font-family:Segoe UI,Roboto,Arial,sans-serif;color:{textColor}">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{background};padding:24px 12px">
                <tr><td align="center">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background:{surface};border-radius:8px;overflow:hidden">
                    <tr><td style="background:{appBar};color:#ffffff;padding:18px 24px;font-size:18px;font-weight:bold">{WebUtility.HtmlEncode(profile.DisplayName)}</td></tr>
                    <tr><td style="padding:24px;font-size:15px;line-height:1.5">{paragraphs}</td></tr>
                    <tr><td style="padding:14px 24px;border-top:1px solid #e5e7eb;font-size:12px;color:#6b7280">{footer}</td></tr>
                  </table>
                </td></tr>
              </table>
            </body></html>
            """;
    }

    private static string Format(string culture, string template, params object[] args) =>
        string.Format(CultureInfo.GetCultureInfo(culture), template, args);

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex Code();
}
