using Mantenio.Domain.Ledger;
using Shouldly;

namespace Mantenio.Domain.Tests.Ledger;

public sealed class LedgerAllocatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private static OpenCharge Charge(int period, decimal outstanding, ChargeType type = ChargeType.Maintenance) =>
        new(Guid.NewGuid(), period, type, T0, outstanding);

    private static AvailablePayment Payment(decimal remaining, int minutes = 0) => new(Guid.NewGuid(), T0.AddMinutes(minutes), remaining);

    [Fact]
    public void An_exact_payment_pays_one_month()
    {
        var nov = Charge(202611, 180m);
        var pay = Payment(180m);

        var plan = LedgerAllocator.Settle([nov], [pay]);

        plan.ShouldBe([new PlannedAllocation(pay.PaymentProofId, nov.ChargeId, 180m)]);
    }

    [Fact]
    public void A_partial_payment_leaves_the_month_partially_paid()
    {
        var nov = Charge(202611, 180m);

        var plan = LedgerAllocator.Settle([nov], [Payment(100m)]);

        plan.Sum(p => p.Amount).ShouldBe(100m);
        Mantenio.Domain.Ledger.Charge.StatusFor(180m, 100m).ShouldBe(ChargeStatus.PartiallyPaid);
    }

    [Fact]
    public void A_big_payment_covers_the_oldest_months_first_and_the_rest_is_credit()
    {
        var dec = Charge(202612, 180m);
        var oct = Charge(202610, 180m);
        var nov = Charge(202611, 180m);
        var pay = Payment(500m);

        var plan = LedgerAllocator.Settle([dec, oct, nov], [pay]);

        plan.Select(p => (p.ChargeId, p.Amount)).ShouldBe([(oct.ChargeId, 180m), (nov.ChargeId, 180m), (dec.ChargeId, 140m)]);
    }

    [Fact]
    public void Credit_without_debts_stays_unallocated()
    {
        LedgerAllocator.Settle([], [Payment(180m)]).ShouldBeEmpty();
    }

    [Fact]
    public void Older_payments_are_used_first_and_opening_debt_goes_before_the_fee_of_the_same_month()
    {
        var fee = Charge(202611, 180m);
        var opening = Charge(202611, 300m, ChargeType.Opening);
        var older = Payment(100m, minutes: 0);
        var newer = Payment(500m, minutes: 10);

        var plan = LedgerAllocator.Settle([fee, opening], [newer, older]);

        plan.ShouldBe(
        [
            new PlannedAllocation(older.PaymentProofId, opening.ChargeId, 100m),
            new PlannedAllocation(newer.PaymentProofId, opening.ChargeId, 200m),
            new PlannedAllocation(newer.PaymentProofId, fee.ChargeId, 180m),
        ]);
    }

    [Fact]
    public void Two_residents_paying_the_same_month_leave_the_second_payment_as_credit()
    {
        var nov = Charge(202611, 180m);
        var first = Payment(180m, minutes: 0);
        var second = Payment(180m, minutes: 5);

        var plan = LedgerAllocator.Settle([nov], [first, second]);

        plan.ShouldHaveSingleItem().PaymentProofId.ShouldBe(first.PaymentProofId);
    }
}
