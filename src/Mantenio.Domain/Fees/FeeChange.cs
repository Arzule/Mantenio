using Mantenio.Domain.Common;

namespace Mantenio.Domain.Fees;

public enum FeeScope
{
    Global = 0,
    Apartment = 1,
}

/// <summary>
/// A change of the maintenance fee, effective from a period on. Never edited or deleted: the history is the record.
/// For an apartment, <see cref="NewAmount"/> null removes its special fee (it goes back to the global one).
/// </summary>
public sealed class FeeChange
{
    private FeeChange() { } // EF Core

    private FeeChange(FeeScope scope, Guid? apartmentId, decimal? oldAmount, decimal? newAmount, Period effectivePeriod,
        string reason, Guid changedById, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (newAmount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newAmount));
        }

        Id = Guid.CreateVersion7();
        Scope = scope;
        ApartmentId = apartmentId;
        OldAmount = oldAmount;
        NewAmount = newAmount;
        EffectivePeriod = effectivePeriod.Value;
        Reason = reason.Trim();
        ChangedById = changedById;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public FeeScope Scope { get; private set; }
    public Guid? ApartmentId { get; private set; }
    public decimal? OldAmount { get; private set; }
    public decimal? NewAmount { get; private set; }

    /// <summary>First period (yyyymm) the new amount applies to.</summary>
    public int EffectivePeriod { get; private set; }

    public string Reason { get; private set; } = "";
    public Guid ChangedById { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static FeeChange Global(decimal oldAmount, decimal newAmount, Period effectivePeriod, string reason, Guid changedById, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newAmount);
        return new FeeChange(FeeScope.Global, null, oldAmount, newAmount, effectivePeriod, reason, changedById, now);
    }

    public static FeeChange ForApartment(Guid apartmentId, decimal? oldAmount, decimal? newAmount, Period effectivePeriod, string reason,
        Guid changedById, DateTimeOffset now) =>
        new(FeeScope.Apartment, apartmentId, oldAmount, newAmount, effectivePeriod, reason, changedById, now);
}

/// <summary>Which fee applies to an apartment in a period: its own special fee if any, otherwise the global fee.</summary>
public static class FeeResolver
{
    public static decimal GlobalFee(Period period, decimal initialFee, IEnumerable<FeeChange> changes) =>
        Latest(changes.Where(c => c.Scope == FeeScope.Global), period)?.NewAmount ?? initialFee;

    /// <summary>The apartment's special fee in force for the period, or null when it pays the global fee.</summary>
    public static decimal? ApartmentFee(Period period, Guid apartmentId, IEnumerable<FeeChange> changes) =>
        Latest(changes.Where(c => c.Scope == FeeScope.Apartment && c.ApartmentId == apartmentId), period)?.NewAmount;

    public static decimal Resolve(Period period, Guid apartmentId, decimal initialFee, IReadOnlyCollection<FeeChange> changes) =>
        ApartmentFee(period, apartmentId, changes) ?? GlobalFee(period, initialFee, changes);

    /// <summary>Latest change effective at the period; ties go to the most recently recorded one.</summary>
    private static FeeChange? Latest(IEnumerable<FeeChange> changes, Period period) =>
        changes
            .Where(c => c.EffectivePeriod <= period.Value)
            .OrderBy(c => c.EffectivePeriod)
            .ThenBy(c => c.CreatedAt)
            .LastOrDefault();
}
