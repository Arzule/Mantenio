using Mantenio.Domain.Common;

namespace Mantenio.Domain.Ledger;

public enum ChargeType
{
    /// <summary>Debt brought from the paper ledger before the system started (paid first).</summary>
    Opening = 0,

    /// <summary>The monthly maintenance fee.</summary>
    Maintenance = 1,

    /// <summary>Late fee on an overdue charge (<see cref="Charge.ParentChargeId"/>).</summary>
    LateFee = 2,
}

public enum ChargeStatus
{
    Pending = 0,
    PartiallyPaid = 1,
    Paid = 2,
}

/// <summary>
/// One debt of an apartment (a month's fee, an opening balance or a late fee). Charges are never edited or deleted;
/// how much is paid comes from the active <see cref="PaymentAllocation"/> rows.
/// </summary>
public sealed class Charge
{
    private Charge() { } // EF Core

    private Charge(Guid apartmentId, Period period, ChargeType type, decimal amount, DateOnly dueDate, Guid? parentChargeId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        Id = Guid.CreateVersion7();
        ApartmentId = apartmentId;
        Period = period.Value;
        Type = type;
        Amount = decimal.Round(amount, 2);
        DueDate = dueDate;
        ParentChargeId = parentChargeId;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ApartmentId { get; private set; }

    /// <summary>yyyymm of the month the charge belongs to.</summary>
    public int Period { get; private set; }

    public ChargeType Type { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly DueDate { get; private set; }
    public Guid? ParentChargeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Charge Maintenance(Guid apartmentId, Period period, decimal amount, DateOnly dueDate, DateTimeOffset now) =>
        new(apartmentId, period, ChargeType.Maintenance, amount, dueDate, null, now);

    public static Charge Opening(Guid apartmentId, Period period, decimal amount, DateOnly dueDate, DateTimeOffset now) =>
        new(apartmentId, period, ChargeType.Opening, amount, dueDate, null, now);

    public static Charge LateFee(Charge parent, decimal amount, DateOnly dueDate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new Charge(parent.ApartmentId, Common.Period.FromValue(parent.Period), ChargeType.LateFee, amount, dueDate, parent.Id, now);
    }

    public static ChargeStatus StatusFor(decimal amount, decimal paid) =>
        paid <= 0 ? ChargeStatus.Pending : paid >= amount ? ChargeStatus.Paid : ChargeStatus.PartiallyPaid;
}
