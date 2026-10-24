using Mantenio.Domain.Common;
using Shouldly;

namespace Mantenio.Domain.Tests.Common;

public sealed class PeriodTests
{
    [Fact]
    public void Value_round_trips_as_yyyymm()
    {
        var period = new Period(2026, 10);

        period.Value.ShouldBe(202610);
        Period.FromValue(202610).ShouldBe(period);
        period.ToString().ShouldBe("202610");
    }

    [Theory]
    [InlineData(2026, 12, 2027, 1)]
    [InlineData(2026, 1, 2026, 2)]
    public void Next_rolls_over_the_year(int year, int month, int nextYear, int nextMonth) =>
        new Period(year, month).Next().ShouldBe(new Period(nextYear, nextMonth));

    [Fact]
    public void Previous_rolls_back_the_year() =>
        new Period(2027, 1).Previous().ShouldBe(new Period(2026, 12));

    [Fact]
    public void DayOfMonth_clamps_to_the_last_day()
    {
        new Period(2026, 2).DayOfMonth(31).ShouldBe(new DateOnly(2026, 2, 28));
        new Period(2028, 2).DayOfMonth(31).ShouldBe(new DateOnly(2028, 2, 29));
        new Period(2026, 10).DayOfMonth(10).ShouldBe(new DateOnly(2026, 10, 10));
    }

    [Fact]
    public void Periods_are_ordered_chronologically()
    {
        (new Period(2026, 12) < new Period(2027, 1)).ShouldBeTrue();
        new[] { new Period(2027, 1), new Period(2026, 3), new Period(2026, 12) }
            .Order()
            .ShouldBe([new Period(2026, 3), new Period(2026, 12), new Period(2027, 1)]);
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(1999, 5)]
    public void Invalid_periods_are_rejected(int year, int month) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new Period(year, month));
}
