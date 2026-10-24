namespace Mantenio.Domain.Ledger;

/// <summary>A charge with what is still owed on it.</summary>
public sealed record OpenCharge(Guid ChargeId, int Period, ChargeType Type, DateTimeOffset CreatedAt, decimal Outstanding);

/// <summary>An approved payment with what is still not applied to any charge (the apartment's credit).</summary>
public sealed record AvailablePayment(Guid PaymentProofId, DateTimeOffset ApprovedAt, decimal Remaining);

public sealed record PlannedAllocation(Guid PaymentProofId, Guid ChargeId, decimal Amount);

/// <summary>
/// Pure FIFO allocation for one apartment: the oldest payment money pays the oldest debt first
/// (by period; within a period opening balance, then the fee, then late fees). Whatever is left is credit,
/// which is never stored: it is simply the unallocated part of approved payments.
/// </summary>
public static class LedgerAllocator
{
    public static IReadOnlyList<PlannedAllocation> Settle(IEnumerable<OpenCharge> charges, IEnumerable<AvailablePayment> payments)
    {
        var debts = charges
            .Where(c => c.Outstanding > 0)
            .OrderBy(c => c.Period).ThenBy(c => c.Type).ThenBy(c => c.CreatedAt)
            .Select(c => new Balance(c.ChargeId, c.Outstanding))
            .ToList();
        var money = payments
            .Where(p => p.Remaining > 0)
            .OrderBy(p => p.ApprovedAt)
            .Select(p => new Balance(p.PaymentProofId, p.Remaining))
            .ToList();

        var plan = new List<PlannedAllocation>();
        int d = 0, m = 0;
        while (d < debts.Count && m < money.Count)
        {
            var amount = Math.Min(debts[d].Amount, money[m].Amount);
            plan.Add(new PlannedAllocation(money[m].Id, debts[d].Id, amount));
            debts[d].Amount -= amount;
            money[m].Amount -= amount;
            if (debts[d].Amount == 0)
            {
                d++;
            }

            if (money[m].Amount == 0)
            {
                m++;
            }
        }

        return plan;
    }

    private sealed class Balance(Guid id, decimal amount)
    {
        public Guid Id { get; } = id;
        public decimal Amount { get; set; } = amount;
    }
}
