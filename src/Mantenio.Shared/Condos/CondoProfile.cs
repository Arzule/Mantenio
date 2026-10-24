using System.Reflection;
using System.Text.Json;

namespace Mantenio.Shared.Condos;

/// <summary>
/// Per-condo configuration that lives in code (condos/&lt;slug&gt;/condo.json), selected at build time.
/// Operational settings that admins change (fees, late-fee %, bank accounts) live in the database instead.
/// </summary>
public sealed record CondoProfile
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string ShortName { get; init; }
    public string OrganizationAcronym { get; init; } = "";
    public string Address { get; init; } = "";
    public string ApiBaseUrl { get; init; } = "";
    public string Culture { get; init; } = "es-PE";
    public string Currency { get; init; } = "PEN";
    public string TimeZone { get; init; } = "America/Lima";
    public string TowerLabel { get; init; } = "Torre";
    public CondoBilling Billing { get; init; } = new();
    public required CondoTheme Theme { get; init; }
    public required CondoReceiptSettings Receipt { get; init; }
    public required CondoAndroidSettings Android { get; init; }
    public CondoModules Modules { get; init; } = new();
    public int FileRetentionMonths { get; init; } = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<CondoProfile> Embedded = new(LoadEmbedded);

    /// <summary>The profile compiled into this build (see Directory.Build.props, property <c>Condo</c>).</summary>
    public static CondoProfile Current => Embedded.Value;

    /// <summary>The condo logo as an SVG data URI, usable directly in an <c>img</c> tag.</summary>
    public static string LogoDataUri => LazyLogo.Value;

    /// <summary>The condo logo as SVG markup (for PDFs).</summary>
    public static string LogoSvg => LazyLogoSvg.Value;

    private static readonly Lazy<string> LazyLogoSvg = new(() => System.Text.Encoding.UTF8.GetString(ReadResourceBytes("condo-logo.svg")));

    private static readonly Lazy<string> LazyLogo = new(() =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(ReadResourceBytes("condo-logo.svg")));

    public static CondoProfile Parse(string json) =>
        JsonSerializer.Deserialize<CondoProfile>(json, JsonOptions)
        ?? throw new InvalidOperationException("Condo profile JSON is empty.");

    private static CondoProfile LoadEmbedded()
    {
        var bytes = ReadResourceBytes("condo.json");
        return JsonSerializer.Deserialize<CondoProfile>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("Embedded condo profile is empty.");
    }

    private static byte[] ReadResourceBytes(string logicalName)
    {
        var assembly = typeof(CondoProfile).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>
/// Initial billing settings, copied to the database the first time (afterwards admins change them in the app).
/// <see cref="DueMonthOffset"/> 1 means the fee of month M is due on <see cref="DueDay"/> of M+1.
/// </summary>
public sealed record CondoBilling
{
    public decimal MonthlyFee { get; init; }
    public int DueDay { get; init; } = 1;
    public int DueMonthOffset { get; init; } = 1;

    /// <summary>yyyymm of the first month tracked by the system.</summary>
    public int TrackingStartPeriod { get; init; }
}

public sealed record CondoTheme
{
    public required string Primary { get; init; }
    public string? PrimaryDarken { get; init; }
    public required string Secondary { get; init; }
    public string? Tertiary { get; init; }
    public string? AppBar { get; init; }
    public string? Background { get; init; }
    public string? Surface { get; init; }
    public string? TextPrimary { get; init; }
}

public sealed record CondoReceiptSettings
{
    public required string Series { get; init; }
    public required string Concept { get; init; }
    public required string SignerTitle { get; init; }
}

public sealed record CondoAndroidSettings
{
    public required string ApplicationId { get; init; }
    public required string AppTitle { get; init; }
}

public sealed record CondoModules
{
    public bool LateFees { get; init; } = true;
    public bool PushNotifications { get; init; } = true;
    public bool EmailReminders { get; init; } = true;
}
