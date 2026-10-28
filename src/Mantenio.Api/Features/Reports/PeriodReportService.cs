using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Domain.Common;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Reports;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Reports;

/// <summary>A charge of the period with what had been paid on it at the close date.</summary>
public sealed record PeriodChargeLine(
    string Tower, int TowerOrder, string Apartment, string Number, int Period, ChargeType Type, decimal Amount, decimal PaidAtClose,
    DateOnly DueDate)
{
    public decimal Outstanding => Amount - PaidAtClose;
}

/// <summary>A payment of the period (by operation date), whatever its state.</summary>
public sealed record PeriodPaymentLine(
    DateOnly OperationDate, string Tower, string Apartment, string Payer, decimal Declared, decimal? Approved, string Bank, string Operation,
    PaymentProofStatus Status, string? Receipt, string RegisteredBy, DateTimeOffset? ReviewedAtLocal, string? ReviewedBy, string? Reason);

/// <summary>Everything the board audits for a range of months; see <see cref="ReportFiles.PeriodExcel"/>.</summary>
public sealed record PeriodReport(
    int From, int To, DateOnly CloseDate, IReadOnlyList<PeriodChargeLine> Charges, IReadOnlyList<PeriodPaymentLine> Payments,
    DelinquencyReport DelinquencyAtClose);

/// <summary>
/// The audit report of a range of months (e.g. the five years of the previous board): the charges of those months
/// with what was paid on them by the close date, every payment made in those months (approved, pending, rejected,
/// voided or historical) and the overdue debt at the close. Rebuilt from the ledger as it was at the close date.
/// </summary>
internal sealed class PeriodReportService(AppDbContext db, DelinquencyService delinquency, CondoClock clock)
{
    /// <summary>The most months one report covers (the file retention period).</summary>
    public const int MaxMonths = 120;

    public async Task<PeriodReport> BuildAsync(Period from, Period to, CancellationToken cancellationToken)
    {
        var lastDay = to.Next().FirstDay.AddDays(-1);
        var closeDate = lastDay < clock.Today ? lastDay : clock.Today;
        var closeUtc = CondoClock.EndOfDayUtc(closeDate);

        var apartments = await db.Apartments.AsNoTracking()
            .Select(a => new { a.Id, a.Code, a.Number, Tower = a.Tower!.Name, TowerOrder = a.Tower.SortOrder })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var (fromValue, toValue) = (from.Value, to.Value);
        var charges = await db.Charges.AsNoTracking()
            .Where(c => c.Period >= fromValue && c.Period <= toValue)
            .Select(c => new { c.Id, c.ApartmentId, c.Period, c.Type, c.Amount, c.DueDate, c.CreatedAt })
            .ToListAsync(cancellationToken);
        var chargeIds = charges.Select(c => c.Id).ToList();
        var allocations = await db.PaymentAllocations.AsNoTracking()
            .Where(a => chargeIds.Contains(a.ChargeId))
            .Select(a => new LedgerAllocation(a.ChargeId, a.Amount, a.CreatedAt, a.ReversedAt))
            .ToListAsync(cancellationToken);
        var paid = DebtCalculator.BalancesAt(
                charges.Select(c => new LedgerCharge(c.Id, c.Amount, c.DueDate, c.CreatedAt)), allocations, closeUtc)
            .ToDictionary(b => b.ChargeId, b => b.Paid);

        var chargeLines = charges
            .Select(c =>
            {
                var apartment = apartments[c.ApartmentId];
                return new PeriodChargeLine(apartment.Tower, apartment.TowerOrder, apartment.Code, apartment.Number, c.Period, c.Type,
                    c.Amount, paid.GetValueOrDefault(c.Id), c.DueDate);
            })
            .OrderBy(l => l.TowerOrder).ThenBy(l => l.Tower, StringComparer.Ordinal)
            .ThenBy(l => l.Number.Length).ThenBy(l => l.Number, StringComparer.Ordinal)
            .ThenBy(l => l.Period).ThenBy(l => l.Type)
            .ToList();

        var fromDate = from.FirstDay;
        var proofs = await (
                from p in db.PaymentProofs.AsNoTracking()
                where p.OperationDate >= fromDate && p.OperationDate <= closeDate
                join s in db.Users on p.SubmittedById equals s.Id
                join r in db.Users on p.ReviewedById equals r.Id into reviewers
                from r in reviewers.DefaultIfEmpty()
                join v in db.Users on p.VoidedById equals v.Id into voiders
                from v in voiders.DefaultIfEmpty()
                join rc in db.Receipts on p.Id equals rc.PaymentProofId into receipts
                from rc in receipts.DefaultIfEmpty()
                orderby p.OperationDate, p.SubmittedAt
                select new
                {
                    Proof = p,
                    Submitter = s.FirstName + " " + s.LastName,
                    Reviewer = r == null ? null : r.FirstName + " " + r.LastName,
                    Voider = v == null ? null : v.FirstName + " " + v.LastName,
                    Receipt = rc == null ? null : rc.Number,
                })
            .ToListAsync(cancellationToken);

        var paymentLines = proofs
            .Select(x =>
            {
                var p = x.Proof;
                var apartment = apartments[p.ApartmentId];
                var registeredByAdmin = p.PayerName is not null || p.Status == PaymentProofStatus.Historical;
                return new PeriodPaymentLine(
                    p.OperationDate, apartment.Tower, apartment.Code, p.PayerName ?? (registeredByAdmin ? "" : x.Submitter),
                    p.DeclaredAmount, p.ApprovedAmount, p.BankName, p.OperationNumber, p.Status, x.Receipt ?? p.PaperReceiptNumber,
                    x.Submitter,
                    p.VoidedAt is { } voided ? CondoClock.ToLocal(voided) : p.ReviewedAt is { } reviewed ? CondoClock.ToLocal(reviewed) : null,
                    x.Voider ?? x.Reviewer, p.VoidReason ?? p.RejectionReason);
            })
            .ToList();

        return new PeriodReport(from.Value, to.Value, closeDate, chargeLines, paymentLines,
            await delinquency.BuildAsync(closeDate, cancellationToken));
    }
}
