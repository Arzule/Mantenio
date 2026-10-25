namespace Mantenio.Domain.Ledger;

/// <summary>
/// Late fees (recargo por mora): one per overdue monthly fee, once, as a % of that fee (no compound interest).
/// The percentage is 0 by default and only the founder changes it. Opening debt and late fees themselves get none.
/// </summary>
public static class LateFeePolicy
{
    public static bool Applies(ChargeType type) => type == ChargeType.Maintenance;

    /// <summary>The late fee for <paramref name="chargeAmount"/>, rounded to cents; 0 when the percentage is 0.</summary>
    public static decimal Amount(decimal chargeAmount, decimal percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chargeAmount);
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        return decimal.Round(chargeAmount * percent / 100m, 2, MidpointRounding.AwayFromZero);
    }
}
