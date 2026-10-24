namespace Mantenio.Domain.Condo;

public sealed record ApartmentListEntry(string Tower, string Number);

/// <summary>
/// Parses the apartment list CSV (<c>tower,number</c> per line, optional header, <c>#</c> comments).
/// Used to seed the condo structure and, later, by the admin CSV import.
/// </summary>
public static class ApartmentList
{
    public static IReadOnlyList<ApartmentListEntry> Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var entries = new List<ApartmentListEntry>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        var headerAllowed = true;

        foreach (var rawLine in csv.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                throw new FormatException($"Line {lineNumber}: expected 'tower,number'.");
            }

            var isHeader = headerAllowed
                && parts[0].Equals("tower", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("number", StringComparison.OrdinalIgnoreCase);
            headerAllowed = false;
            if (isHeader)
            {
                continue;
            }

            if (!codes.Add(Apartment.BuildCode(parts[0], parts[1])))
            {
                throw new FormatException($"Line {lineNumber}: apartment {parts[0]}-{parts[1]} is repeated.");
            }

            entries.Add(new ApartmentListEntry(parts[0], parts[1]));
        }

        return entries;
    }
}
