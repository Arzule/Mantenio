using Mantenio.Shared.Condos;

namespace Mantenio.UI.Components;

/// <summary>Times are stored in UTC and shown in the condo time zone (America/Lima for the pilot).</summary>
public static class CondoTime
{
    private static readonly TimeZoneInfo Zone = FindZone(CondoProfile.Current.TimeZone);

    public static DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            // Lima has no daylight saving time, so a fixed offset is exact if the device lacks the zone data.
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(-5), id, id);
        }
    }
}
