using Mantenio.Domain.Common;
using Mantenio.Domain.Fees;
using Shouldly;

namespace Mantenio.Domain.Tests.Fees;

public sealed class FeeResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Apartment = Guid.NewGuid();

    [Fact]
    public void Without_changes_the_initial_fee_applies() =>
        FeeResolver.Resolve(new Period(2026, 11), Apartment, 180m, []).ShouldBe(180m);

    [Fact]
    public void A_global_change_applies_from_its_period_on_and_not_before()
    {
        FeeChange[] changes = [FeeChange.Global(180m, 200m, new Period(2027, 1), "Aumento de vigilancia", Admin, Now)];

        FeeResolver.Resolve(new Period(2026, 12), Apartment, 180m, changes).ShouldBe(180m);
        FeeResolver.Resolve(new Period(2027, 1), Apartment, 180m, changes).ShouldBe(200m);
        FeeResolver.Resolve(new Period(2027, 6), Apartment, 180m, changes).ShouldBe(200m);
    }

    [Fact]
    public void An_apartment_fee_wins_over_the_global_fee_until_it_is_removed()
    {
        FeeChange[] changes =
        [
            FeeChange.Global(180m, 200m, new Period(2027, 1), "Aumento", Admin, Now),
            FeeChange.ForApartment(Apartment, null, 90m, new Period(2026, 12), "Local comercial pequeño", Admin, Now),
            FeeChange.ForApartment(Apartment, 90m, null, new Period(2027, 3), "Vuelve a la cuota general", Admin, Now.AddMinutes(1)),
        ];

        FeeResolver.Resolve(new Period(2026, 11), Apartment, 180m, changes).ShouldBe(180m);
        FeeResolver.Resolve(new Period(2026, 12), Apartment, 180m, changes).ShouldBe(90m);
        FeeResolver.Resolve(new Period(2027, 2), Apartment, 180m, changes).ShouldBe(90m);
        FeeResolver.Resolve(new Period(2027, 3), Apartment, 180m, changes).ShouldBe(200m);
    }

    [Fact]
    public void Another_apartments_fee_does_not_apply() =>
        FeeResolver.Resolve(new Period(2027, 1), Apartment, 180m,
            [FeeChange.ForApartment(Guid.NewGuid(), null, 50m, new Period(2026, 12), "Otro", Admin, Now)]).ShouldBe(180m);

    [Fact]
    public void For_the_same_period_the_latest_recorded_change_wins()
    {
        FeeChange[] changes =
        [
            FeeChange.Global(180m, 210m, new Period(2027, 1), "Primero", Admin, Now),
            FeeChange.Global(210m, 205m, new Period(2027, 1), "Corrección", Admin, Now.AddHours(1)),
        ];

        FeeResolver.GlobalFee(new Period(2027, 1), 180m, changes).ShouldBe(205m);
    }

    [Fact]
    public void Changes_need_a_reason_and_a_non_negative_amount()
    {
        Should.Throw<ArgumentException>(() => FeeChange.Global(180m, 200m, new Period(2027, 1), " ", Admin, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => FeeChange.Global(180m, -1m, new Period(2027, 1), "x", Admin, Now));
    }
}
