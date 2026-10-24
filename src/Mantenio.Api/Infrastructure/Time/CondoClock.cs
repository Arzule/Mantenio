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
