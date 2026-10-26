using Mantenio.Shared.Ledger;

namespace Mantenio.UI.Components;

/// <summary>Pre-fills the payment form with what was read from the voucher, never overwriting what the person typed.</summary>
public static class ProofScanFill
{
    /// <param name="date">The form starts with today's date, so today counts as "not chosen yet".</param>
    public static (decimal? Amount, DateTime? Date, string Operation) Apply(
        ProofScanResult scan, decimal? amount, DateTime? date, string operation)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var dateUntouched = date is null || date.Value.Date == DateTime.Today;
        return (
            amount ?? scan.Amount,
            dateUntouched && scan.OperationDate is { } read ? read.ToDateTime(TimeOnly.MinValue) : date,
            string.IsNullOrWhiteSpace(operation) ? scan.OperationNumber ?? operation : operation);
    }
}
