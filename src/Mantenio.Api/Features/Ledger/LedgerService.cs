using System.Globalization;
using Mantenio.Api.Features.Settings;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Resources;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Common;
using Mantenio.Domain.Fees;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Ledger;

/// <summary>Ledger operations on top of the pure rules (<see cref="LedgerAllocator"/>). Callers own the transaction.</summary>
internal sealed class LedgerService(AppDbContext db, TimeProvider time, IFileStorage storage)
{
    /// <summary>
    /// Applies the apartment's unallocated approved money to its open charges, oldest first, and records one audit
    /// event per allocation (it shows up in the charge timeline). Approval/void changes must be saved before.
    /// </summary>
    public async Task<IReadOnlyList<PaymentAllocation>> SettleAsync(Guid apartmentId, AuditSource source, Guid? actorId, CancellationToken cancellationToken)
    {
        var charges = await db.Charges.Where(c => c.ApartmentId == apartmentId).ToListAsync(cancellationToken);
        var proofs = await db.PaymentProofs
            .Where(p => p.ApartmentId == apartmentId && p.Status == PaymentProofStatus.Approved)
            .ToListAsync(cancellationToken);
        var proofIds = proofs.Select(p => p.Id).ToList();
        var active = await db.PaymentAllocations
            .Where(a => proofIds.Contains(a.PaymentProofId) && a.ReversedAt == null)
            .ToListAsync(cancellationToken);

        var paidByCharge = active.GroupBy(a => a.ChargeId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var usedByProof = active.GroupBy(a => a.PaymentProofId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var plan = LedgerAllocator.Settle(
            charges.Select(c => new OpenCharge(c.Id, c.Period, c.Type, c.CreatedAt, c.Amount - paidByCharge.GetValueOrDefault(c.Id))),
            proofs.Select(p => new AvailablePayment(p.Id, p.ReviewedAt!.Value, p.ApprovedAmount!.Value - usedByProof.GetValueOrDefault(p.Id))));

        var now = time.GetUtcNow();
        var created = new List<PaymentAllocation>();
        foreach (var item in plan)
        {
            var allocation = new PaymentAllocation(item.PaymentProofId, item.ChargeId, item.Amount, now);
            created.Add(allocation);
            db.PaymentAllocations.Add(allocation);
            db.AuditEvents.Add(new AuditEvent(
                AuditEntities.Charge, item.ChargeId.ToString(), "PaymentApplied", source, source == AuditSource.System ? null : actorId, now,
                newValues: AuditLog.Json(new { item.PaymentProofId, item.Amount }), apartmentId: apartmentId, chargeId: item.ChargeId));
        }

        return created;
    }

    /// <summary>Creates the monthly fee of every non-frozen apartment for the period (idempotent), then applies credit.</summary>
    public async Task<GenerateChargesResult> GenerateChargesAsync(Period period, AuditSource source, Guid? actorId, CancellationToken cancellationToken)
    {
        var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, cancellationToken);
        var apartments = await db.Apartments.AsNoTracking().Select(a => new { a.Id, a.IsFrozen }).ToListAsync(cancellationToken);
        var existing = (await db.Charges
            .Where(c => c.Period == period.Value && c.Type == ChargeType.Maintenance)
            .Select(c => c.ApartmentId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var changes = await db.FeeChanges.AsNoTracking().ToListAsync(cancellationToken);
        var dueDate = settings.DueDateFor(period);
        var now = time.GetUtcNow();
        var created = new List<Charge>();

        foreach (var apartment in apartments.Where(a => !a.IsFrozen && !existing.Contains(a.Id)))
        {
            var fee = FeeResolver.Resolve(period, apartment.Id, settings.MonthlyFee, changes);
            if (fee <= 0)
            {
                continue;
            }

            var charge = Charge.Maintenance(apartment.Id, period, fee, dueDate, now);
            created.Add(charge);
            db.Charges.Add(charge);
            db.AuditEvents.Add(new AuditEvent(
                AuditEntities.Charge, charge.Id.ToString(), "ChargeCreated", source, source == AuditSource.System ? null : actorId, now,
                newValues: AuditLog.Json(new { charge.Period, charge.Amount, charge.DueDate, Type = charge.Type.ToString() }),
                apartmentId: apartment.Id, chargeId: charge.Id));
        }

        await db.SaveChangesAsync(cancellationToken);

        // Apartments that already had credit pay the new fee right away.
        foreach (var apartmentId in await ApartmentsWithCreditAsync(created.Select(c => c.ApartmentId).ToList(), cancellationToken))
        {
            await SettleAsync(apartmentId, source, actorId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new GenerateChargesResult(period.Value, created.Count, apartments.Count(a => a.IsFrozen), existing.Count);
    }

    public async Task<decimal> CreditAsync(Guid apartmentId, CancellationToken cancellationToken)
    {
        var approved = await db.PaymentProofs
            .Where(p => p.ApartmentId == apartmentId && p.Status == PaymentProofStatus.Approved)
            .SumAsync(p => p.ApprovedAmount ?? 0, cancellationToken);
        var allocated = await db.PaymentAllocations
            .Where(a => a.ReversedAt == null && db.PaymentProofs.Any(p => p.Id == a.PaymentProofId && p.ApartmentId == apartmentId))
            .SumAsync(a => a.Amount, cancellationToken);
        return approved - allocated;
    }

    /// <summary>Next gap-free receipt number: the counter row is locked until the approval transaction ends.</summary>
    public async Task<long> NextReceiptSequenceAsync(CancellationToken cancellationToken)
    {
        await CondoSettingsStore.GetOrCreateAsync(db, time, cancellationToken);
        return await db.Database
            .SqlQuery<long>($"UPDATE \"CondoSettings\" SET \"LastReceiptSequence\" = \"LastReceiptSequence\" + 1 RETURNING \"LastReceiptSequence\" AS \"Value\"")
            .AsAsyncEnumerable()
            .SingleAsync(cancellationToken);
    }

    /// <summary>Renders the receipt PDF (paid or voided) and stores it; returns the object key.</summary>
    public async Task<string> StoreReceiptPdfAsync(Receipt receipt, PaymentProof proof, bool voided, CancellationToken cancellationToken)
    {
        var apartment = await db.Apartments.AsNoTracking().Include(a => a.Tower).SingleAsync(a => a.Id == proof.ApartmentId, cancellationToken);
        // Registered by an admin: made out to the resident who paid, not to the admin who typed it in.
        var payer = proof.PayerName
            ?? await db.Users.Where(u => u.Id == proof.SubmittedById).Select(u => u.FirstName + " " + u.LastName).SingleAsync(cancellationToken);
        var approver = await db.Users.Where(u => u.Id == proof.ReviewedById).Select(u => u.FirstName + " " + u.LastName).SingleOrDefaultAsync(cancellationToken);

        // Months this payment paid when it was issued (reversed allocations included, so a voided receipt still says what it covered).
        var allocations = await db.PaymentAllocations.Where(a => a.PaymentProofId == proof.Id).ToListAsync(cancellationToken);
        allocations.AddRange(db.PaymentAllocations.Local.Where(a => a.PaymentProofId == proof.Id && !allocations.Contains(a)));
        var chargeIds = allocations.Select(a => a.ChargeId).Distinct().ToList();
        var periods = await db.Charges.Where(c => chargeIds.Contains(c.Id)).Select(c => c.Period).Distinct().OrderBy(p => p).ToListAsync(cancellationToken);
        var credit = proof.ApprovedAmount!.Value - allocations.Where(a => a.CreatedAt <= receipt.IssuedAt.AddSeconds(1)).Sum(a => a.Amount);

        var issuedLocal = CondoClock.ToLocal(receipt.IssuedAt);
        var pdf = ReceiptPdf.Render(new ReceiptData(
            receipt.Number,
            proof.ApprovedAmount.Value,
            DateOnly.FromDateTime(issuedLocal.DateTime),
            issuedLocal,
            payer,
            apartment.Tower!.Name,
            apartment.Number,
            periods,
            Math.Max(credit, 0),
            proof.OperationDate,
            proof.OperationNumber,
            proof.BankName,
            approver ?? "",
            voided));

        var key = StorageKeys.Receipt(receipt.Number, voided);
        using var stream = new MemoryStream(pdf);
        await storage.PutAsync(key, stream, "application/pdf", cancellationToken);
        receipt.AttachPdf(key);
        return key;
    }

    public static string ReceiptNumber(long sequence) => Receipt.FormatNumber(CondoProfile.Current.Receipt.Series, sequence);

    /// <summary>"recibo-0003-000001.pdf" (Resources/ReceiptTexts.resx).</summary>
    public static string ReceiptFileName(string number) => Format(ReceiptTexts.AttachmentName, number);

    private static string Format(string template, params object[] args) => string.Format(CultureInfo.InvariantCulture, template, args);

    private async Task<List<Guid>> ApartmentsWithCreditAsync(List<Guid> apartmentIds, CancellationToken cancellationToken)
    {
        var approved = await db.PaymentProofs
            .Where(p => apartmentIds.Contains(p.ApartmentId) && p.Status == PaymentProofStatus.Approved)
            .GroupBy(p => p.ApartmentId)
            .Select(g => new { ApartmentId = g.Key, Total = g.Sum(p => p.ApprovedAmount ?? 0) })
            .ToListAsync(cancellationToken);
        var result = new List<Guid>();
        foreach (var row in approved)
        {
            if (await CreditAsync(row.ApartmentId, cancellationToken) > 0)
            {
                result.Add(row.ApartmentId);
            }
        }

        return result;
    }
}
