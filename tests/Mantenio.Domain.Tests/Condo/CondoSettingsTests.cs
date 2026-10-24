using Mantenio.Domain.Common;
using Mantenio.Domain.Condo;
using Shouldly;

namespace Mantenio.Domain.Tests.Condo;

public sealed class CondoSettingsTests
{
    [Fact]
    public void A_charge_can_be_due_on_the_first_day_of_the_next_month()
    {
        var settings = new CondoSettings(180m, dueDay: 1, dueMonthOffset: 1, new Period(2026, 11));

        settings.DueDateFor(new Period(2026, 10)).ShouldBe(new DateOnly(2026, 11, 1));
        settings.DueDateFor(new Period(2026, 12)).ShouldBe(new DateOnly(2027, 1, 1));
    }

    [Fact]
    public void A_charge_can_be_due_within_its_own_month()
    {
        var settings = new CondoSettings(180m, dueDay: 10, dueMonthOffset: 0, new Period(2026, 11));

        settings.DueDateFor(new Period(2026, 11)).ShouldBe(new DateOnly(2026, 11, 10));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(29, 0)]
    [InlineData(1, 2)]
    [InlineData(1, -1)]
    public void Invalid_due_rules_are_rejected(int day, int offset) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new CondoSettings(180m, day, offset, new Period(2026, 11)));

    [Fact]
    public void The_late_fee_starts_at_zero_and_stays_between_0_and_100_percent()
    {
        var settings = new CondoSettings(180m, 1, 1, new Period(2026, 11));
        settings.LateFeePercent.ShouldBe(0m);

        settings.SetLateFeePercent(5.555m);
        settings.LateFeePercent.ShouldBe(5.56m);

        Should.Throw<ArgumentOutOfRangeException>(() => settings.SetLateFeePercent(-1m));
        Should.Throw<ArgumentOutOfRangeException>(() => settings.SetLateFeePercent(101m));
    }

    [Fact]
    public void Admin_seats_are_limited() =>
        new CondoSettings(180m, 1, 1, new Period(2026, 11)).HasAdminSeat(10).ShouldBeFalse();
}
