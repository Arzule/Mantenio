using System.Globalization;

namespace Mantenio.Domain.Common;

/// <summary>
/// A billing month. Stored as an int in <c>yyyymm</c> form (e.g. 202610).
/// </summary>
public readonly record struct Period : IComparable<Period>
{
    public int Year { get; }
    public int Month { get; }

    public Period(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 2000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        Year = year;
        Month = month;
    }

    public int Value => (Year * 100) + Month;

    public static Period FromValue(int value) => new(value / 100, value % 100);

    public static bool TryFromValue(int value, out Period period)
    {
        var (year, month) = (value / 100, value % 100);
        var valid = year is >= 2000 and <= 9999 && month is >= 1 and <= 12;
        period = valid ? new Period(year, month) : default;
        return valid;
    }

    public static Period FromDate(DateOnly date) => new(date.Year, date.Month);

    public Period Next() => Month == 12 ? new(Year + 1, 1) : new(Year, Month + 1);

    public Period Previous() => Month == 1 ? new(Year - 1, 12) : new(Year, Month - 1);

    public DateOnly FirstDay => new(Year, Month, 1);

    /// <summary>The given day of this month, clamped to the last day (e.g. day 31 in February).</summary>
    public DateOnly DayOfMonth(int day) =>
        new(Year, Month, Math.Clamp(day, 1, DateTime.DaysInMonth(Year, Month)));

    public int CompareTo(Period other) => Value.CompareTo(other.Value);

    public static bool operator <(Period left, Period right) => left.Value < right.Value;
    public static bool operator >(Period left, Period right) => left.Value > right.Value;
    public static bool operator <=(Period left, Period right) => left.Value <= right.Value;
    public static bool operator >=(Period left, Period right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString("000000", CultureInfo.InvariantCulture);
}
