using Mantenio.Domain.Ledger;
using Shouldly;

namespace Mantenio.Domain.Tests.Ledger;

public sealed class LateFeePolicyTests
{
    [Theory]
    [InlineData(180, 0, 0)]
    [InlineData(180, 5, 9)]
    [InlineData(165, 3, 4.95)]
    [InlineData(150.50, 2.5, 3.76)]
    public void The_late_fee_is_a_rounded_percentage_of_the_fee(decimal amount, decimal percent, decimal expected) =>
        LateFeePolicy.Amount(amount, percent).ShouldBe(expected);

    [Fact]
    public void Only_monthly_fees_get_late_fees() =>
        Enum.GetValues<ChargeType>().Where(LateFeePolicy.Applies).ShouldBe([ChargeType.Maintenance]);
}
