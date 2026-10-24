using Mantenio.Domain.Ledger;
using Shouldly;

namespace Mantenio.Domain.Tests.Ledger;

public sealed class DebtCalculatorTests
{
    private static readonly DateTimeOffset Nov1 = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private static readonly LedgerCharge November = new(Guid.NewGuid(), 180m, new DateOnly(2026, 12, 1), Nov1);
    private static readonly LedgerCharge December = new(Guid.NewGuid(), 180m, new DateOnly(2027, 1, 1), Nov1.AddMonths(1));

    [Fact]
    public void Debt_on_a_past_date_ignores_later_payments_and_later_charges()
    {
        var paidLate = new LedgerAllocation(November.ChargeId, 180m, Nov1.AddDays(40), null);

        var onDec5 = DebtCalculator.BalancesAt([November, December], [paidLate], Nov1.AddDays(34));

        onDec5.Count.ShouldBe(2);
        DebtCalculator.OverdueOn(onDec5, new DateOnly(2026, 12, 5)).ShouldBe(180m);
        var today = DebtCalculator.BalancesAt([November, December], [paidLate], Nov1.AddDays(60));
        DebtCalculator.OverdueOn(today, new DateOnly(2026, 12, 31)).ShouldBe(0m);
    }

    [Fact]
    public void A_voided_payment_counted_while_it_was_active()
    {
        var voided = new LedgerAllocation(November.ChargeId, 180m, Nov1.AddDays(5), Nov1.AddDays(45));

        DebtCalculator.OverdueOn(DebtCalculator.BalancesAt([November], [voided], Nov1.AddDays(35)), new DateOnly(2026, 12, 6)).ShouldBe(0m);
        DebtCalculator.OverdueOn(DebtCalculator.BalancesAt([November], [voided], Nov1.AddDays(46)), new DateOnly(2026, 12, 17)).ShouldBe(180m);
    }

    [Fact]
    public void Charges_not_yet_due_are_not_overdue()
    {
        var balances = DebtCalculator.BalancesAt([November], [new LedgerAllocation(November.ChargeId, 50m, Nov1, null)], Nov1.AddDays(1));

        balances.ShouldHaveSingleItem().Outstanding.ShouldBe(130m);
        DebtCalculator.OverdueOn(balances, new DateOnly(2026, 11, 2)).ShouldBe(0m);
        DebtCalculator.OverdueOn(balances, new DateOnly(2026, 12, 2)).ShouldBe(130m);
    }
}
