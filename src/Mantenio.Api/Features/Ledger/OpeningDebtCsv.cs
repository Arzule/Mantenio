using System.Globalization;
using Mantenio.Domain.Common;

namespace Mantenio.Api.Features.Ledger;

/// <summary>
/// Opening debt from the paper ledger: one line per apartment and month, <c>apartmentCode,period,amount</c>
/// (e.g. <c>A-101,202609,180.00</c>; period also as <c>2026-09</c>; optional header; <c>;</c> also accepted).
/// All or nothing: any error and nothing is imported.
/// </summary>
internal static class OpeningDebtCsv
{
    public sealed record Row(Guid ApartmentId, Period Period, decimal Amount);

    public sealed record Result(IReadOnlyList<Row> Rows, IReadOnlyList<string> Errors);

    public static Result Parse(string csv, IReadOnlyDictionary<string, Guid> apartmentsByCode, IReadOnlySet<(Guid ApartmentId, int Period)> existing)
    {
        var rows = new List<Row>();
        var errors = new List<string>();
        var seen = new HashSet<(Guid, int)>();
        var lines = (csv ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            var lineNumber = index + 1;
            if (line.Length == 0 || (index == 0 && line.StartsWith("apartment", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var parts = line.Split(line.Contains(';', StringComparison.Ordinal) ? ';' : ',').Select(p => p.Trim()).ToArray();
            if (parts.Length != 3)
            {
                errors.Add(Error(lineNumber, "columns"));
                continue;
            }

            if (!apartmentsByCode.TryGetValue(parts[0], out var apartmentId))
            {
                errors.Add(Error(lineNumber, "apartment"));
                continue;
            }

            if (!int.TryParse(parts[1].Replace("-", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || !Period.TryFromValue(value, out var period))
            {
                errors.Add(Error(lineNumber, "period"));
                continue;
            }

            if (!decimal.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                || amount <= 0 || amount > 99_999 || decimal.Round(amount, 2) != amount)
            {
                errors.Add(Error(lineNumber, "amount"));
                continue;
            }

            if (existing.Contains((apartmentId, period.Value)) || !seen.Add((apartmentId, period.Value)))
            {
                errors.Add(Error(lineNumber, "duplicate"));
                continue;
            }

            rows.Add(new Row(apartmentId, period, amount));
        }

        if (rows.Count == 0 && errors.Count == 0)
        {
            errors.Add(Error(0, "empty"));
        }

        return new Result(rows, errors);
    }

    /// <summary>"line:problem" (the UI translates the problem).</summary>
    private static string Error(int line, string problem) => string.Create(CultureInfo.InvariantCulture, $"{line}:{problem}");
}
