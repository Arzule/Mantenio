using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Common;
using Mantenio.Domain.Condo;
using Mantenio.Shared.Condos;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Settings;

/// <summary>The single settings row; created from the condo profile ("billing") the first time it is needed.</summary>
internal static class CondoSettingsStore
{
    public static async Task<CondoSettings> GetOrCreateAsync(AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var settings = await db.CondoSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        var billing = CondoProfile.Current.Billing;
        var start = billing.TrackingStartPeriod > 0
            ? Period.FromValue(billing.TrackingStartPeriod)
            : Period.FromDate(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
        settings = new CondoSettings(billing.MonthlyFee, billing.DueDay, billing.DueMonthOffset, start);
        db.CondoSettings.Add(settings);
        db.AuditEvents.Add(new AuditEvent(
            AuditEntities.CondoSettings, CondoSettings.SingletonId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "CreatedFromProfile", AuditSource.System, null, time.GetUtcNow(),
            newValues: AuditLog.Json(new { settings.MonthlyFee, settings.DueDay, settings.DueMonthOffset, settings.TrackingStartPeriod })));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (DbUpdateException)
        {
            // Another request created it at the same moment.
            db.ChangeTracker.Clear();
            return await db.CondoSettings.SingleAsync(cancellationToken);
        }
    }
}
