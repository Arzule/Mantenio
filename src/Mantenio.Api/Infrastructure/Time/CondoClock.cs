using Mantenio.Domain.Common;
using Mantenio.Shared.Condos;

namespace Mantenio.Api.Infrastructure.Time;

/// <summary>"Today" and "this month" in the condo's time zone (America/Lima), not in UTC.</summary>
public sealed class CondoClock(TimeProvider time)
{
    private static readonly TimeZoneInfo Zone = FindZone(CondoProfile.Current.TimeZone);

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    public Period CurrentPeriod => Period.FromDate(Today);

    public static DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>The last instant of <paramref name="date"/> in the condo time zone, in UTC.</summary>
    public static DateTimeOffset EndOfDayUtc(DateOnly date)
    {
        var nextMidnight = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(nextMidnight, Zone.GetUtcOffset(nextMidnight)).ToUniversalTime().AddTicks(-1);
    }

    /// <summary>The first instant of <paramref name="date"/> in the condo time zone, in UTC.</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, Zone.GetUtcOffset(midnight)).ToUniversalTime();
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            // Lima has no daylight saving time; a fixed offset is exact if the container lacks tz data.
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(-5), id, id);
        }
    }
}
