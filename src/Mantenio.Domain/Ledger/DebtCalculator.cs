namespace Mantenio.Domain.Ledger;

public sealed record LedgerCharge(Guid ChargeId, decimal Amount, DateOnly DueDate, DateTimeOffset CreatedAt);

public sealed record LedgerAllocation(Guid ChargeId, decimal Amount, DateTimeOffset CreatedAt, DateTimeOffset? ReversedAt);

public sealed record ChargeBalance(Guid ChargeId, decimal Amount, decimal Paid, DateOnly DueDate)
{
    public decimal Outstanding => Amount - Paid;
}

/// <summary>
/// Rebuilds what each charge owed at a past moment: only charges that existed then, and only allocations made
/// before it and not yet reversed at it. Debt at a date = outstanding of the charges due before that date.
/// </summary>
public static class DebtCalculator
{
    public static IReadOnlyList<ChargeBalance> BalancesAt(
        IEnumerable<LedgerCharge> charges, IEnumerable<LedgerAllocation> allocations, DateTimeOffset at)
    {
        var paid = allocations
            .Where(a => a.CreatedAt <= at && (a.ReversedAt is null || a.ReversedAt > at))
            .GroupBy(a => a.ChargeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        return charges
            .Where(c => c.CreatedAt <= at)
            .Select(c => new ChargeBalance(c.ChargeId, c.Amount, Math.Min(c.Amount, paid.GetValueOrDefault(c.ChargeId)), c.DueDate))
            .ToList();
    }

    /// <summary>Unpaid amount of the charges whose due date is before <paramref name="date"/> (overdue on that day).</summary>
    public static decimal OverdueOn(IEnumerable<ChargeBalance> balances, DateOnly date) =>
        balances.Where(b => b.DueDate < date).Sum(b => b.Outstanding);
}
