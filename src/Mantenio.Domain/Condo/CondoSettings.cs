namespace Mantenio.Domain.Condo;

/// <summary>
/// Operational settings managed by admins (single row). Branding lives in the condo profile, not here.
/// </summary>
public sealed class CondoSettings
{
    public const int SingletonId = 1;
    public const int DefaultMaxAdmins = 10;

    private CondoSettings() { } // EF Core

    public CondoSettings(decimal monthlyFee, int dueDay, int trackingStartPeriod)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(monthlyFee);
        ArgumentOutOfRangeException.ThrowIfLessThan(dueDay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueDay, 28);
        Id = SingletonId;
        MonthlyFee = monthlyFee;
        DueDay = dueDay;
        TrackingStartPeriod = trackingStartPeriod;
    }

    public int Id { get; private set; }
    public decimal MonthlyFee { get; private set; }

    /// <summary>Day of the month when the maintenance charge is due (1–28 so it exists every month).</summary>
    public int DueDay { get; private set; }

    /// <summary>Late fee as a percentage of the overdue charge. Only the founder admin can change it.</summary>
    public decimal LateFeePercent { get; private set; }

    public int MaxAdmins { get; private set; } = DefaultMaxAdmins;

    /// <summary>Active admins (founder included) can never exceed <see cref="MaxAdmins"/>.</summary>
    public bool HasAdminSeat(int activeAdmins) => activeAdmins < MaxAdmins;

    /// <summary>First period (yyyymm) tracked by the system; older debt is loaded as opening balance.</summary>
    public int TrackingStartPeriod { get; private set; }
}
