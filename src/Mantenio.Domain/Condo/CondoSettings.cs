using Mantenio.Domain.Common;

namespace Mantenio.Domain.Condo;

/// <summary>
/// Operational settings managed by admins (single row). Branding lives in the condo profile, not here.
/// The global fee history lives in <see cref="Fees.FeeChange"/>; <see cref="MonthlyFee"/> is the fee in force
/// from <see cref="TrackingStartPeriod"/> until the first change.
/// </summary>
public sealed class CondoSettings
{
    public const int SingletonId = 1;
    public const int DefaultMaxAdmins = 10;
    public const decimal MaxLateFeePercent = 100m;
    public const int MaxBoardEmails = 10;

    private CondoSettings() { } // EF Core

    public CondoSettings(decimal monthlyFee, int dueDay, int dueMonthOffset, Period trackingStartPeriod)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(monthlyFee);
        Id = SingletonId;
        MonthlyFee = monthlyFee;
        SetDueRule(dueDay, dueMonthOffset);
        TrackingStartPeriod = trackingStartPeriod.Value;
    }

    public int Id { get; private set; }

    /// <summary>Initial global fee (from the condo profile); later changes are <see cref="Fees.FeeChange"/> rows.</summary>
    public decimal MonthlyFee { get; private set; }

    /// <summary>Day of the month when a charge is due (1–28 so it exists every month).</summary>
    public int DueDay { get; private set; }

    /// <summary>
    /// 0: the charge of month M is due on <see cref="DueDay"/> of M. 1: it is due on <see cref="DueDay"/> of M+1
    /// (El Prado: "the fee of October is due on November 1st").
    /// </summary>
    public int DueMonthOffset { get; private set; }

    /// <summary>Late fee as a percentage of the overdue charge. Only the founder admin can change it.</summary>
    public decimal LateFeePercent { get; private set; }

    public int MaxAdmins { get; private set; } = DefaultMaxAdmins;

    /// <summary>First period (yyyymm) tracked by the system; older debt is loaded as opening balance.</summary>
    public int TrackingStartPeriod { get; private set; }

    /// <summary>Last receipt number issued. Only incremented in SQL inside the approval transaction (gap-free).</summary>
    public long LastReceiptSequence { get; private set; }

    /// <summary>Board members who get the monthly summary by email (comma-separated). Only the founder changes it.</summary>
    public string BoardEmails { get; private set; } = "";

    public IReadOnlyList<string> BoardEmailList =>
        BoardEmails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Replaces the board emails (trimmed, lower case, no duplicates, at most <see cref="MaxBoardEmails"/>).</summary>
    public void SetBoardEmails(IEnumerable<string> emails)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var list = emails.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        if (list.Count > MaxBoardEmails || list.Any(e => e.Contains(',', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Too many board emails or an invalid one.", nameof(emails));
        }

        BoardEmails = string.Join(',', list);
    }

    /// <summary>Active admins (founder included) can never exceed <see cref="MaxAdmins"/>.</summary>
    public bool HasAdminSeat(int activeAdmins) => activeAdmins < MaxAdmins;

    public DateOnly DueDateFor(Period period)
    {
        var duePeriod = period;
        for (var i = 0; i < DueMonthOffset; i++)
        {
            duePeriod = duePeriod.Next();
        }

        return duePeriod.DayOfMonth(DueDay);
    }

    public void SetDueRule(int dueDay, int dueMonthOffset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dueDay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueDay, 28);
        ArgumentOutOfRangeException.ThrowIfLessThan(dueMonthOffset, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueMonthOffset, 1);
        DueDay = dueDay;
        DueMonthOffset = dueMonthOffset;
    }

    public void SetLateFeePercent(decimal percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, MaxLateFeePercent);
        LateFeePercent = decimal.Round(percent, 2);
    }
}
