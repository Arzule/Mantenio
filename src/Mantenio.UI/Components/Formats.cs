using System.Globalization;
using Mantenio.Shared.Condos;

namespace Mantenio.UI.Components;

/// <summary>How money, months and dates are written in the UI (condo culture, es-PE: "S/ 180.00", "noviembre 2026").</summary>
public static class Formats
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);

    public static string Money(decimal amount) => amount.ToString("C2", Culture);

    /// <summary>yyyymm → "Noviembre 2026".</summary>
    public static string Month(int period)
    {
        var name = new DateTime(period / 100, period % 100, 1).ToString("MMMM yyyy", Culture);
        return char.ToUpper(name[0], Culture) + name[1..];
    }

    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", Culture);

    public static string Date(DateTimeOffset utc) => CondoTime.ToLocal(utc).ToString("dd/MM/yyyy", Culture);

    public static string DateTime(DateTimeOffset utc) => CondoTime.ToLocal(utc).ToString("dd/MM/yyyy HH:mm", Culture);

    /// <summary>The next <paramref name="count"/> months after <paramref name="current"/> (yyyymm).</summary>
    public static IEnumerable<int> NextMonths(int current, int count)
    {
        var year = current / 100;
        var month = current % 100;
        for (var i = 0; i < count; i++)
        {
            month++;
            if (month > 12)
            {
                month = 1;
                year++;
            }

            yield return (year * 100) + month;
        }
    }

    public static string Format(string template, params object?[] args) => string.Format(Culture, template, args);
}
