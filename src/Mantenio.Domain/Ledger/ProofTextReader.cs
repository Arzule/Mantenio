using System.Globalization;
using System.Text.RegularExpressions;

namespace Mantenio.Domain.Ledger;

/// <summary>What could be read from a payment voucher; any field may be missing.</summary>
public sealed record ProofTextFields(decimal? Amount, DateOnly? OperationDate, string? OperationNumber);

/// <summary>
/// Finds the amount, date and operation number in the text lines of a voucher (OCR output, any bank). It only
/// pre-fills the form: the resident and the reviewing admin still check every value against the image.
/// </summary>
public static partial class ProofTextReader
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ene"] = 1, ["enero"] = 1, ["jan"] = 1, ["january"] = 1,
        ["feb"] = 2, ["febrero"] = 2, ["february"] = 2,
        ["mar"] = 3, ["marzo"] = 3, ["march"] = 3,
        ["abr"] = 4, ["abril"] = 4, ["apr"] = 4, ["april"] = 4,
        ["may"] = 5, ["mayo"] = 5,
        ["jun"] = 6, ["junio"] = 6, ["june"] = 6,
        ["jul"] = 7, ["julio"] = 7, ["july"] = 7,
        ["ago"] = 8, ["agosto"] = 8, ["aug"] = 8, ["august"] = 8,
        ["set"] = 9, ["sep"] = 9, ["sept"] = 9, ["setiembre"] = 9, ["septiembre"] = 9, ["september"] = 9,
        ["oct"] = 10, ["octubre"] = 10, ["october"] = 10,
        ["nov"] = 11, ["noviembre"] = 11, ["november"] = 11,
        ["dic"] = 12, ["diciembre"] = 12, ["dec"] = 12, ["december"] = 12,
    };

    /// <param name="lines">The recognized lines, top to bottom.</param>
    /// <param name="today">Today in the condo's time zone: later dates and dates over a year old are ignored.</param>
    public static ProofTextFields Read(IReadOnlyList<string> lines, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var clean = lines.Select(l => (l ?? "").Trim()).Where(l => l.Length > 0).ToList();
        return new ProofTextFields(FindAmount(clean), FindDate(clean, today), FindOperationNumber(clean));
    }

    // ---- Amount ----------------------------------------------------------------------------------------------

    private static decimal? FindAmount(List<string> lines)
    {
        decimal? labeled = null, withCurrency = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (FeeLabel().IsMatch(line))
            {
                continue;
            }

            // "S/ 180.00" on one line, or "S/" (often read as "SI") on one line and "180.00" on the next.
            var currency = CurrencyAmount().Match(line);
            decimal? value = currency.Success ? ParseMoney(currency.Groups["n"].Value)
                : i + 1 < lines.Count && CurrencyOnly().IsMatch(line) && BareAmount().Match(lines[i + 1]) is { Success: true } next
                    ? ParseMoney(next.Groups["n"].Value)
                    : null;

            if (AmountLabel().IsMatch(line))
            {
                // The label with the value on the same line or on the next one.
                value ??= BareAmount().Match(line) is { Success: true } same ? ParseMoney(same.Groups["n"].Value)
                    : i + 1 < lines.Count ? ParseLeadingMoney(lines[i + 1]) : null;
                labeled ??= value;
            }

            withCurrency ??= value;
        }

        return labeled ?? withCurrency;
    }

    private static decimal? ParseLeadingMoney(string line)
    {
        var currency = CurrencyAmount().Match(line);
        if (currency.Success)
        {
            return ParseMoney(currency.Groups["n"].Value);
        }

        var bare = BareAmount().Match(line);
        return bare.Success && bare.Index == 0 ? ParseMoney(bare.Groups["n"].Value) : null;
    }

    /// <summary>"1,250.00", "1 250.00", "180.00", "180,00" or "180".</summary>
    private static decimal? ParseMoney(string text)
    {
        var digits = text.Replace(" ", "", StringComparison.Ordinal);
        var lastComma = digits.LastIndexOf(',');
        var lastDot = digits.LastIndexOf('.');
        if (lastComma > lastDot && digits.Length - lastComma == 3)
        {
            digits = digits.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        }
        else
        {
            digits = digits.Replace(",", "", StringComparison.Ordinal);
        }

        return decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
               && value is > 0 and <= 99_999
            ? decimal.Round(value, 2)
            : null;
    }

    // ---- Date ------------------------------------------------------------------------------------------------

    private static DateOnly? FindDate(List<string> lines, DateOnly today)
    {
        DateOnly? labeled = null, first = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var found = ParseDate(lines[i], today);
            if (DateLabel().IsMatch(lines[i]))
            {
                found ??= i + 1 < lines.Count ? ParseDate(lines[i + 1], today) : null;
                labeled ??= found;
            }

            first ??= found;
        }

        return labeled ?? first;
    }

    private static DateOnly? ParseDate(string line, DateOnly today)
    {
        foreach (Match m in NumericDate().Matches(line))
        {
            var year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (Valid(year < 100 ? 2000 + year : year, Number(m.Groups["m"]), Number(m.Groups["d"]), today) is { } date)
            {
                return date;
            }
        }

        foreach (Match m in WordDate().Matches(line))
        {
            if (!Months.TryGetValue(m.Groups["m"].Value.TrimEnd('.'), out var month))
            {
                continue;
            }

            // Without a year (e.g. "25 oct, 9:00 pm") it is the latest such day that is not in the future.
            var year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) : today.Year;
            var date = Valid(year, month, Number(m.Groups["d"]), today);
            if (date is null && !m.Groups["y"].Success)
            {
                date = Valid(year - 1, month, Number(m.Groups["d"]), today);
            }

            if (date is not null)
            {
                return date;
            }
        }

        return null;
    }

    private static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    private static DateOnly? Valid(int year, int month, int day, DateOnly today)
    {
        if (month is < 1 or > 12 || day < 1 || year < 2000 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var date = new DateOnly(year, month, day);
        return date <= today && date >= today.AddYears(-1) ? date : null;
    }

    // ---- Operation number ------------------------------------------------------------------------------------

    private static string? FindOperationNumber(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var label = OperationLabel().Match(lines[i]);
            if (!label.Success)
            {
                continue;
            }

            var rest = lines[i][(label.Index + label.Length)..];
            if (OperationValue(rest) is { } sameLine)
            {
                return sameLine;
            }

            if (i + 1 < lines.Count && OperationValue(lines[i + 1]) is { } nextLine)
            {
                return nextLine;
            }
        }

        return null;
    }

    /// <summary>The first token with at least 4 digits (a short letter prefix allowed, e.g. "A0012345"); spaces inside are joined.</summary>
    private static string? OperationValue(string text)
    {
        // A date or an amount is never the operation number.
        if (NumericDate().IsMatch(text) || CurrencyAmount().IsMatch(text) || BareAmount().IsMatch(text))
        {
            return null;
        }

        var m = OperationToken().Match(text);
        if (!m.Success)
        {
            return null;
        }

        var value = m.Value.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return value.Count(char.IsAsciiDigit) >= 4 && value.Length <= 40 ? value : null;
    }

    // ---- Patterns --------------------------------------------------------------------------------------------

    [GeneratedRegex(@"(?<![A-Za-z])(?:S\s?/\.?|S\.\s?/|PEN|SI)\s*(?<n>\d{1,3}(?:[,\s]\d{3})*(?:[.,]\d{2})?|\d+(?:[.,]\d{2})?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyAmount();

    [GeneratedRegex(@"^(?:S\s?/\.?|S\.\s?/|PEN|SI)$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyOnly();

    [GeneratedRegex(@"(?<![\d/.-])(?<n>\d{1,3}(?:,\d{3})*[.,]\d{2}|\d+[.,]\d{2})(?![\d/-])")]
    private static partial Regex BareAmount();

    [GeneratedRegex(@"\b(monto|importe|total|pagaste|transferiste|enviaste|yapeaste|abonado|dep[oó]sito de)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmountLabel();

    [GeneratedRegex(@"\b(comisi[oó]n|itf|cargo por)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FeeLabel();

    [GeneratedRegex(@"(?<!\d)(?<d>\d{1,2})[/.-](?<m>\d{1,2})[/.-](?<y>\d{4}|\d{2})(?!\d)")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"(?<!\d)(?<d>\d{1,2})\s*(?:de\s+)?(?<m>[a-záéíóú]{3,10}\.?)(?:\s*(?:de|del|,)?\s*(?<y>\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex WordDate();

    [GeneratedRegex(@"\bfecha\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateLabel();

    [GeneratedRegex(@"\b(n[°ºo]\.?\s*(de\s+)?|nro\.?\s*(de\s+)?|n[uú]mero\s+de\s+|c[oó]d(igo|\.)?\s*(de\s+)?)?\b(operaci[oó]n|oper\.?(\s*banc\.?)?|constancia|transacci[oó]n|movimiento)\s*:?", RegexOptions.IgnoreCase)]
    private static partial Regex OperationLabel();

    [GeneratedRegex(@"\b[A-Z]{0,4}\d[\d -]{2,38}\d\b", RegexOptions.IgnoreCase)]
    private static partial Regex OperationToken();
}
