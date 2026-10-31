using System.Security.Claims;
using Mantenio.Api.Features.Apartments;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Notifications;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Security;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Infrastructure.Vision;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Common;
using Mantenio.Domain.Ledger;
using Mantenio.Domain.Notifications;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mantenio.Api.Features.Ledger;

/// <summary>
/// Payment proofs, their review and the account statement (docs/context.md §4). Approving applies the money FIFO to
/// the apartment's charges and issues a gap-free receipt; voiding reverses it. Every change writes an audit event.
/// </summary>
internal static class LedgerEndpoints
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/api/payments").WithTags("Payments").RequireAuthorization();
        payments.MapPost("/", SubmitAsync).DisableAntiforgery().WithUploadLimits().Produces<PaymentProofItem>();
        payments.MapPost("/scan", ScanAsync).DisableAntiforgery().WithUploadLimits().Produces<ProofScanResult>();
        payments.MapGet("/mine", MineAsync).Produces<IReadOnlyList<PaymentProofItem>>();
        payments.MapGet("/{id:guid}/image", ImageAsync).Produces<FileLink>();
        payments.MapGet("/{id:guid}/receipt", ReceiptAsync).Produces<FileLink>();
        payments.MapGet("/{id:guid}/receipt/pdf", ReceiptPdfAsync);

        app.MapGet("/api/apartments/{id:guid}/statement", StatementAsync).WithTags("Payments").RequireAuthorization()
            .Produces<AccountStatement>();

        // The review queue is shared by admins and the gatehouse (portería).
        var staff = app.MapGroup("/api/admin").WithTags("Payments").RequireAuthorization(MantenioPolicies.StaffOnly);
        staff.MapGet("/payments", ListAsync).Produces<IReadOnlyList<PaymentProofItem>>();
        staff.MapPost("/payments/register", RegisterAsync).DisableAntiforgery().WithUploadLimits().Produces<PaymentProofItem>();
        staff.MapGet("/apartments/{id:guid}/residents", ResidentsAsync).Produces<IReadOnlyList<string>>();
        staff.MapPost("/payments/{id:guid}/approve", ApproveAsync).Produces<PaymentProofItem>();
        staff.MapPost("/payments/{id:guid}/reject", RejectAsync).Produces<PaymentProofItem>();
        staff.MapPost("/payments/{id:guid}/void", VoidAsync).Produces<PaymentProofItem>();
        staff.MapGet("/payments/{id:guid}/timeline", ProofTimelineAsync).Produces<IReadOnlyList<TimelineEntry>>();

        var admin = app.MapGroup("/api/admin").WithTags("Payments").RequireAuthorization(MantenioPolicies.AdminOnly);
        admin.MapPost("/payments/historical", RegisterHistoricalAsync).DisableAntiforgery().WithUploadLimits().Produces<PaymentProofItem>();
        admin.MapPost("/payments/{id:guid}/historical", UpdateHistoricalAsync).DisableAntiforgery().WithUploadLimits().Produces<PaymentProofItem>();
        admin.MapGet("/charges/{id:guid}/timeline", TimelineAsync).Produces<IReadOnlyList<TimelineEntry>>();
        admin.MapPost("/charges/opening", ImportOpeningAsync).Produces<ImportOpeningDebtResult>();
        admin.MapPost("/charges/generate", GenerateAsync).RequireAuthorization(MantenioPolicies.FounderOnly)
            .Produces<GenerateChargesResult>();

        return app;
    }

    // ---- Resident -------------------------------------------------------------------------------------------

    private static async Task<IResult> SubmitAsync(
        [FromForm] decimal amount, [FromForm] DateOnly operationDate, [FromForm] string bankName, [FromForm] string operationNumber,
        IFormFile image, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CondoClock clock, IFileStorage storage,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var apartmentId = await db.Users.Where(u => u.Id == userId).Select(u => u.ApartmentId).SingleAsync(cancellationToken);
        if (apartmentId is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.ApartmentRequired);
        }

        if (await ValidateProofAsync(db, clock, amount, operationDate, bankName, operationNumber, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var key = await StoreImageAsync(image, apartmentId.Value, storage, cancellationToken);
        if (key is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidImage);
        }

        var now = time.GetUtcNow();
        var proof = new PaymentProof(apartmentId.Value, userId, amount, operationDate, bankName, operationNumber, key, now);
        db.PaymentProofs.Add(proof);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.PaymentProof, proof.Id.ToString(), "Submitted", AuditSource.Resident, userId, now,
            newValues: AuditLog.Json(new { proof.DeclaredAmount, proof.OperationDate, proof.BankName, proof.OperationNumber }),
            apartmentId: apartmentId));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiProblems.Conflict(ErrorCodes.DuplicateOperationNumber);
        }

        return TypedResults.Ok(await ItemAsync(db, proof.Id, cancellationToken));
    }

    /// <summary>
    /// Reads amount, date and operation number from the voucher image to pre-fill the form (resident upload and admin
    /// registration). Nothing is stored; the person and the reviewing admin still check the values.
    /// </summary>
    private static async Task<IResult> ScanAsync(IFormFile image, IProofScanner scanner, CondoClock clock, CancellationToken cancellationToken)
    {
        var jpeg = await ReadImageAsync(image, cancellationToken);
        if (jpeg is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidImage);
        }

        var lines = scanner.IsEnabled ? await scanner.ReadLinesAsync(jpeg, cancellationToken) : null;
        if (lines is null)
        {
            return TypedResults.Ok(new ProofScanResult(false, null, null, null));
        }

        var fields = ProofTextReader.Read(lines, clock.Today);
        return TypedResults.Ok(new ProofScanResult(true, fields.Amount, fields.OperationDate, fields.OperationNumber));
    }

    private static async Task<IResult> MineAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var apartmentId = await db.Users.Where(u => u.Id == userId).Select(u => u.ApartmentId).SingleAsync(cancellationToken);
        return apartmentId is null
            ? TypedResults.Ok(Array.Empty<PaymentProofItem>())
            : TypedResults.Ok(await ItemsAsync(db, db.PaymentProofs.Where(p => p.ApartmentId == apartmentId), cancellationToken));
    }

    private static async Task<IResult> ImageAsync(
        Guid id, ClaimsPrincipal principal, AppDbContext db, IFileStorage storage, CancellationToken cancellationToken)
    {
        var proof = await db.PaymentProofs.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (proof is not { HasImage: true } || !await CanViewProofAsync(db, principal, proof.ApartmentId, cancellationToken))
        {
            return ApiProblems.NotFound();
        }

        return TypedResults.Ok(new FileLink(storage.GetReadUrl(proof.ImageObjectKey, LinkLifetime)));
    }

    private static async Task<IResult> ReceiptAsync(
        Guid id, ClaimsPrincipal principal, AppDbContext db, IFileStorage storage, CancellationToken cancellationToken)
    {
        var proof = await db.PaymentProofs.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (proof is null || !await CanViewProofAsync(db, principal, proof.ApartmentId, cancellationToken))
        {
            return ApiProblems.NotFound();
        }

        var key = await db.Receipts.Where(r => r.PaymentProofId == id).Select(r => r.PdfObjectKey).SingleOrDefaultAsync(cancellationToken);
        return key is null ? ApiProblems.NotFound() : TypedResults.Ok(new FileLink(storage.GetReadUrl(key, LinkLifetime)));
    }

    private static async Task<IResult> StatementAsync(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CondoClock clock, LedgerService ledger, CancellationToken cancellationToken)
    {
        var apartment = await db.Apartments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (apartment is null || !await ApartmentAccess.CanAccessAsync(db, principal, id, cancellationToken))
        {
            return ApiProblems.NotFound();
        }

        var charges = await db.Charges.AsNoTracking().Where(c => c.ApartmentId == id).ToListAsync(cancellationToken);
        var chargeIds = charges.Select(c => c.Id).ToList();
        var paid = await db.PaymentAllocations.AsNoTracking()
            .Where(a => chargeIds.Contains(a.ChargeId) && a.ReversedAt == null)
            .GroupBy(a => a.ChargeId)
            .Select(g => new { ChargeId = g.Key, Paid = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.ChargeId, x => x.Paid, cancellationToken);

        var today = clock.Today;
        var items = charges
            .OrderByDescending(c => c.Period).ThenByDescending(c => c.Type).ThenByDescending(c => c.CreatedAt)
            .Select(c =>
            {
                var chargePaid = paid.GetValueOrDefault(c.Id);
                var status = Charge.StatusFor(c.Amount, chargePaid);
                return new ChargeItem(c.Id, c.Period, c.Type.ToString(), c.Amount, chargePaid, (DebtStatus)(int)status, c.DueDate,
                    status != ChargeStatus.Paid && c.DueDate < today);
            })
            .ToList();

        var payments = await ItemsAsync(db, db.PaymentProofs.Where(p => p.ApartmentId == id), cancellationToken);
        return TypedResults.Ok(new AccountStatement(
            id,
            apartment.Code,
            apartment.IsFrozen,
            items,
            payments,
            await ledger.CreditAsync(id, cancellationToken),
            items.Where(i => i.Overdue).Sum(i => i.Amount - i.Paid),
            items.Sum(i => i.Amount - i.Paid)));
    }

    // ---- Admin review ---------------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(ProofStatus? status, AppDbContext db, CancellationToken cancellationToken)
    {
        var query = db.PaymentProofs.AsQueryable();
        if (status is not null)
        {
            var domainStatus = (PaymentProofStatus)(int)status.Value;
            query = query.Where(p => p.Status == domainStatus);
        }

        var items = await ItemsAsync(db, query, cancellationToken);

        // The queue is worked oldest first; history newest first.
        return TypedResults.Ok(status == ProofStatus.Pending ? items.OrderBy(i => i.SubmittedAt).ToList() : items);
    }

    private static async Task<IResult> ApproveAsync(
        Guid id, ApproveProofRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, LedgerService ledger,
        NotificationService notifications, ReceiptEmails emails, CancellationToken cancellationToken)
    {
        var adminId = principal.GetUserId();
        var source = AuditLog.SourceFor(principal);
        Receipt? issued = null;
        IResult result;
        try
        {
            result = await db.InTransactionAsync<IResult>(async ct =>
            {
                var proof = await db.PaymentProofs.SingleOrDefaultAsync(p => p.Id == id, ct);
                if (await ReviewProblemAsync(db, principal, proof, PaymentProofStatus.Pending, request.RowVersion, ct) is { } problem)
                {
                    return problem;
                }

                var now = time.GetUtcNow();
                db.Entry(proof!).Property<uint>("RowVersion").OriginalValue = unchecked((uint)request.RowVersion);
                proof!.Approve(request.ApprovedAmount, adminId, now);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.PaymentProof, proof.Id.ToString(), "Approved", source, adminId, now,
                    oldValues: AuditLog.Json(new { Status = nameof(PaymentProofStatus.Pending), proof.DeclaredAmount }),
                    newValues: AuditLog.Json(new { Status = nameof(PaymentProofStatus.Approved), proof.ApprovedAmount }),
                    apartmentId: proof.ApartmentId));
                var receipt = await SettleAndIssueReceiptAsync(db, ledger, notifications, proof, source, adminId, now, ct);
                issued = receipt;
                return TypedResults.Ok(await ItemAsync(db, proof.Id, ct));
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblems.Conflict(ErrorCodes.Conflict);
        }

        if (issued is not null)
        {
            // After the commit: a mail failure must not undo the approval (the receipt is also in the app).
            await emails.SendAsync(issued, cancellationToken);
        }

        return result;
    }

    private static async Task<IResult> RejectAsync(
        Guid id, RejectProofRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time,
        NotificationService notifications, CancellationToken cancellationToken)
    {
        var adminId = principal.GetUserId();
        var source = AuditLog.SourceFor(principal);
        try
        {
            return await db.InTransactionAsync<IResult>(async ct =>
            {
                var proof = await db.PaymentProofs.SingleOrDefaultAsync(p => p.Id == id, ct);
                if (await ReviewProblemAsync(db, principal, proof, PaymentProofStatus.Pending, request.RowVersion, ct) is { } problem)
                {
                    return problem;
                }

                var now = time.GetUtcNow();
                var reason = request.Reason.Trim();
                db.Entry(proof!).Property<uint>("RowVersion").OriginalValue = unchecked((uint)request.RowVersion);
                proof!.Reject(reason, adminId, now);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.PaymentProof, proof.Id.ToString(), "Rejected", source, adminId, now,
                    oldValues: AuditLog.Json(new { Status = nameof(PaymentProofStatus.Pending) }),
                    newValues: AuditLog.Json(new { Status = nameof(PaymentProofStatus.Rejected) }), reason: reason, apartmentId: proof.ApartmentId));
                await notifications.NotifyApartmentAsync(proof.ApartmentId, NotificationType.PaymentRejected,
                    new PaymentNoticeData(proof.Id, proof.DeclaredAmount, null, reason), ct);
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(await ItemAsync(db, proof.Id, ct));
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblems.Conflict(ErrorCodes.Conflict);
        }
    }

    private static async Task<IResult> VoidAsync(
        Guid id, VoidProofRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, LedgerService ledger,
        NotificationService notifications, CancellationToken cancellationToken)
    {
        var adminId = principal.GetUserId();
        var source = AuditLog.SourceFor(principal);
        try
        {
            return await db.InTransactionAsync<IResult>(async ct =>
            {
                var proof = await db.PaymentProofs.SingleOrDefaultAsync(p => p.Id == id, ct);
                if (await ReviewProblemAsync(db, principal, proof, PaymentProofStatus.Approved, null, ct) is { } problem)
                {
                    return problem;
                }

                var now = time.GetUtcNow();
                var reason = request.Reason.Trim();
                var allocations = await db.PaymentAllocations.Where(a => a.PaymentProofId == id && a.ReversedAt == null).ToListAsync(ct);
                foreach (var allocation in allocations)
                {
                    allocation.Reverse(now);
                    db.AuditEvents.Add(new AuditEvent(AuditEntities.Charge, allocation.ChargeId.ToString(), "PaymentReversed", source, adminId, now,
                        oldValues: AuditLog.Json(new { allocation.PaymentProofId, allocation.Amount }), reason: reason,
                        apartmentId: proof!.ApartmentId, chargeId: allocation.ChargeId));
                }

                proof!.Void(reason, adminId, now);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.PaymentProof, proof.Id.ToString(), "Voided", source, adminId, now,
                    oldValues: AuditLog.Json(new { Status = nameof(PaymentProofStatus.Approved), proof.ApprovedAmount }),
                    newValues: AuditLog.Json(new { Status = nameof(PaymentProofStatus.Voided) }), reason: reason, apartmentId: proof.ApartmentId));

                var receipt = await db.Receipts.SingleOrDefaultAsync(r => r.PaymentProofId == id, ct);
                if (receipt is not null)
                {
                    receipt.Void(now);
                    await ledger.StoreReceiptPdfAsync(receipt, proof, voided: true, ct);
                    db.AuditEvents.Add(new AuditEvent(AuditEntities.Receipt, receipt.Id.ToString(), "Voided", source, adminId, now,
                        newValues: AuditLog.Json(new { receipt.Number, receipt.PdfObjectKey }), reason: reason, apartmentId: proof.ApartmentId));
                }

                await db.SaveChangesAsync(ct);

                // The charges are open again: other credit of the apartment pays them first.
                await ledger.SettleAsync(proof.ApartmentId, source, adminId, ct);
                await notifications.NotifyApartmentAsync(proof.ApartmentId, NotificationType.PaymentVoided,
                    new PaymentNoticeData(proof.Id, proof.ApprovedAmount!.Value, receipt?.Number, reason), ct);
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(await ItemAsync(db, proof.Id, ct));
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblems.Conflict(ErrorCodes.Conflict);
        }
    }

    private static async Task<IResult> TimelineAsync(Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Charges.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ApiProblems.NotFound();
        }

        var rows = await (
                from e in db.AuditEvents.AsNoTracking()
                where e.ChargeId == id
                join u in db.Users on e.ActorUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby e.CreatedAt, e.Id
                select new { e.CreatedAt, e.Action, e.Source, FirstName = u == null ? null : u.FirstName, LastName = u == null ? null : u.LastName, e.NewValues, e.OldValues, e.Reason })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(rows
            .Select(r => new TimelineEntry(r.CreatedAt, r.Action, r.Source.ToString(), r.FirstName is null ? null : $"{r.FirstName} {r.LastName}",
                r.NewValues ?? r.OldValues, r.Reason))
            .ToList());
    }

    /// <summary>
    /// Everything that happened to one payment: submission or registration, review, the months it paid, the receipt
    /// and a void. Open to the gatehouse, which has no other access to the audit log.
    /// </summary>
    private static async Task<IResult> ProofTimelineAsync(Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var apartmentId = await db.PaymentProofs.AsNoTracking().Where(p => p.Id == id).Select(p => (Guid?)p.ApartmentId).SingleOrDefaultAsync(cancellationToken);
        if (apartmentId is null)
        {
            return ApiProblems.NotFound();
        }

        var proofKey = id.ToString();
        var receiptKey = (await db.Receipts.Where(r => r.PaymentProofId == id).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken))?.ToString() ?? "";
        var rows = await (
                from e in db.AuditEvents.AsNoTracking()
                where (e.EntityType == AuditEntities.PaymentProof && e.EntityId == proofKey)
                      || (e.EntityType == AuditEntities.Receipt && e.EntityId == receiptKey)
                      || (e.EntityType == AuditEntities.Charge && e.ApartmentId == apartmentId
                          && (e.Action == "PaymentApplied" || e.Action == "PaymentReversed"))
                join u in db.Users on e.ActorUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby e.CreatedAt, e.Id
                select new { e.EntityType, e.CreatedAt, e.Action, e.Source, FirstName = u == null ? null : u.FirstName, LastName = u == null ? null : u.LastName, e.NewValues, e.OldValues, e.Reason })
            .ToListAsync(cancellationToken);

        // Allocations of other payments of the same apartment are left out (each names its proof in the values).
        return TypedResults.Ok(rows
            .Where(r => r.EntityType != AuditEntities.Charge
                        || (r.NewValues ?? r.OldValues ?? "").Contains(proofKey, StringComparison.OrdinalIgnoreCase))
            .Select(r => new TimelineEntry(r.CreatedAt, r.Action, r.Source.ToString(), r.FirstName is null ? null : $"{r.FirstName} {r.LastName}",
                r.NewValues ?? r.OldValues, r.Reason))
            .ToList());
    }

    private static Task<IResult> GenerateAsync(
        GenerateChargesRequest request, ClaimsPrincipal principal, AppDbContext db, LedgerService ledger, CancellationToken cancellationToken)
    {
        if (!Period.TryFromValue(request.Period, out var period))
        {
            return Task.FromResult<IResult>(ApiProblems.BadRequest(ErrorCodes.InvalidPeriod));
        }

        var adminId = principal.GetUserId();
        return db.InTransactionAsync<IResult>(
            async ct => TypedResults.Ok(await ledger.GenerateChargesAsync(period, AuditSource.Admin, adminId, ct)), cancellationToken);
    }

    private static Task<IResult> ImportOpeningAsync(
        ImportOpeningDebtRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, LedgerService ledger,
        CancellationToken cancellationToken) =>
        db.InTransactionAsync<IResult>(async ct =>
        {
            var adminId = principal.GetUserId();
            var apartments = await db.Apartments.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Id, StringComparer.OrdinalIgnoreCase, ct);
            var existing = (await db.Charges.Where(c => c.Type == ChargeType.Opening).Select(c => new { c.ApartmentId, c.Period }).ToListAsync(ct))
                .Select(c => (c.ApartmentId, c.Period)).ToHashSet();
            var parsed = OpeningDebtCsv.Parse(request.Csv, apartments, existing);
            if (parsed.Errors.Count > 0)
            {
                return TypedResults.Ok(new ImportOpeningDebtResult(0, parsed.Errors));
            }

            var settings = await Settings.CondoSettingsStore.GetOrCreateAsync(db, time, ct);
            var now = time.GetUtcNow();
            foreach (var row in parsed.Rows)
            {
                var charge = Charge.Opening(row.ApartmentId, row.Period, row.Amount, settings.DueDateFor(row.Period), now);
                db.Charges.Add(charge);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.Charge, charge.Id.ToString(), "ChargeCreated", AuditSource.Admin, adminId, now,
                    newValues: AuditLog.Json(new { charge.Period, charge.Amount, charge.DueDate, Type = charge.Type.ToString() }),
                    reason: "opening_import", apartmentId: row.ApartmentId, chargeId: charge.Id));
            }

            await db.SaveChangesAsync(ct);
            foreach (var apartmentId in parsed.Rows.Select(r => r.ApartmentId).Distinct())
            {
                await ledger.SettleAsync(apartmentId, AuditSource.Admin, adminId, ct);
            }

            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new ImportOpeningDebtResult(parsed.Rows.Count, []));
        }, cancellationToken);

    // ---- Helpers --------------------------------------------------------------------------------------------

    /// <summary>
    /// The gatehouse or an admin registers the voucher a resident brought in person: it is approved at once and the
    /// receipt is made out to <paramref name="payerName"/>. The screenshot or photo is required; an admin needs an
    /// apartment and cannot register for it, as in the review queue.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        [FromForm] Guid apartmentId, [FromForm] string payerName, [FromForm] decimal amount, [FromForm] DateOnly operationDate,
        [FromForm] string bankName, [FromForm] string operationNumber, IFormFile? image, ClaimsPrincipal principal, AppDbContext db,
        TimeProvider time, CondoClock clock, IFileStorage storage, LedgerService ledger, NotificationService notifications,
        ReceiptEmails emails, CancellationToken cancellationToken)
    {
        var adminId = principal.GetUserId();
        var source = AuditLog.SourceFor(principal);
        if (!await db.Apartments.AnyAsync(a => a.Id == apartmentId, cancellationToken))
        {
            return ApiProblems.BadRequest(ErrorCodes.ApartmentNotFound);
        }

        if (await StaffProblemAsync(db, principal, apartmentId, cancellationToken) is { } staffProblem)
        {
            return staffProblem;
        }

        if (string.IsNullOrWhiteSpace(payerName) || payerName.Trim().Length > 120)
        {
            return ApiProblems.BadRequest(ErrorCodes.ValidationFailed);
        }

        if (await ValidateProofAsync(db, clock, amount, operationDate, bankName, operationNumber, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var key = image is null ? null : await StoreImageAsync(image, apartmentId, storage, cancellationToken);
        if (key is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidImage);
        }

        Receipt? issued = null;
        IResult result;
        try
        {
            result = await db.InTransactionAsync<IResult>(async ct =>
            {
                var now = time.GetUtcNow();
                var proof = PaymentProof.RegisteredByAdmin(apartmentId, adminId, payerName, amount, operationDate, bankName, operationNumber, key, now);
                db.PaymentProofs.Add(proof);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.PaymentProof, proof.Id.ToString(), "RegisteredByAdmin", source, adminId, now,
                    newValues: AuditLog.Json(new { proof.PayerName, proof.DeclaredAmount, proof.OperationDate, proof.BankName, proof.OperationNumber }),
                    apartmentId: apartmentId));
                proof.Approve(amount, adminId, now);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.PaymentProof, proof.Id.ToString(), "Approved", source, adminId, now,
                    newValues: AuditLog.Json(new { Status = nameof(PaymentProofStatus.Approved), proof.ApprovedAmount }), apartmentId: apartmentId));
                issued = await SettleAndIssueReceiptAsync(db, ledger, notifications, proof, source, adminId, now, ct);
                return TypedResults.Ok(await ItemAsync(db, proof.Id, ct));
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiProblems.Conflict(ErrorCodes.DuplicateOperationNumber);
        }

        if (issued is not null)
        {
            await emails.SendAsync(issued, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// An admin loads a payment from before the app (paper records): amount and date are required; payer, bank,
    /// operation number, paper receipt number and photo are optional. It is kept for the history and the audit,
    /// and does not change the debt (no allocation, no receipt).
    /// </summary>
    private static async Task<IResult> RegisterHistoricalAsync(
        [FromForm] Guid apartmentId, [FromForm] decimal amount, [FromForm] DateOnly operationDate, [FromForm] string? payerName,
        [FromForm] string? bankName, [FromForm] string? operationNumber, [FromForm] string? paperReceiptNumber, IFormFile? image,
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CondoClock clock, IFileStorage storage, CancellationToken cancellationToken)
    {
        var adminId = principal.GetUserId();
        if (!await db.Apartments.AnyAsync(a => a.Id == apartmentId, cancellationToken))
        {
            return ApiProblems.BadRequest(ErrorCodes.ApartmentNotFound);
        }

        if (await StaffProblemAsync(db, principal, apartmentId, cancellationToken) is { } staffProblem)
        {
            return staffProblem;
        }

        if (await ValidateHistoricalAsync(db, clock, amount, operationDate, payerName, bankName, operationNumber, paperReceiptNumber, null,
                cancellationToken) is { } invalid)
        {
            return invalid;
        }

        string? key = null;
        if (image is not null && (key = await StoreImageAsync(image, apartmentId, storage, cancellationToken)) is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidImage);
        }

        var now = time.GetUtcNow();
        var proof = PaymentProof.Historical(apartmentId, adminId, amount, operationDate, payerName, bankName, operationNumber, paperReceiptNumber, key, now);
        db.PaymentProofs.Add(proof);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.PaymentProof, proof.Id.ToString(), "HistoricalRegistered", AuditSource.Admin, adminId, now,
            newValues: HistoricalSnapshot(proof), apartmentId: apartmentId));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiProblems.Conflict(ErrorCodes.DuplicateOperationNumber);
        }

        return TypedResults.Ok(await ItemAsync(db, proof.Id, cancellationToken));
    }

    /// <summary>
    /// Corrects a historical payment and/or attaches its photo; a new photo replaces the previous one (whose file is
    /// deleted). Audited with the old and new values.
    /// </summary>
    private static async Task<IResult> UpdateHistoricalAsync(
        Guid id, [FromForm] decimal amount, [FromForm] DateOnly operationDate, [FromForm] string? payerName, [FromForm] string? bankName,
        [FromForm] string? operationNumber, [FromForm] string? paperReceiptNumber, IFormFile? image, ClaimsPrincipal principal,
        AppDbContext db, TimeProvider time, CondoClock clock, IFileStorage storage, CancellationToken cancellationToken)
    {
        var adminId = principal.GetUserId();
        var proof = await db.PaymentProofs.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (proof is null)
        {
            return ApiProblems.NotFound();
        }

        if (proof.Status != PaymentProofStatus.Historical)
        {
            return ApiProblems.Conflict(ErrorCodes.Conflict);
        }

        if (await StaffProblemAsync(db, principal, proof.ApartmentId, cancellationToken) is { } staffProblem)
        {
            return staffProblem;
        }

        if (await ValidateHistoricalAsync(db, clock, amount, operationDate, payerName, bankName, operationNumber, paperReceiptNumber, id,
                cancellationToken) is { } invalid)
        {
            return invalid;
        }

        string? newKey = null;
        if (image is not null && (newKey = await StoreImageAsync(image, proof.ApartmentId, storage, cancellationToken)) is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidImage);
        }

        var old = HistoricalSnapshot(proof);
        proof.SetHistoricalDetails(amount, operationDate, payerName, bankName, operationNumber, paperReceiptNumber);
        var replaced = newKey is null ? null : proof.ReplaceImage(newKey);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.PaymentProof, proof.Id.ToString(), newKey is null ? "HistoricalUpdated" : "HistoricalPhotoAttached",
            AuditSource.Admin, adminId, time.GetUtcNow(), oldValues: old, newValues: HistoricalSnapshot(proof), apartmentId: proof.ApartmentId));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiProblems.Conflict(ErrorCodes.DuplicateOperationNumber);
        }

        if (!string.IsNullOrEmpty(replaced))
        {
            await storage.DeleteAsync(replaced, cancellationToken);
        }

        return TypedResults.Ok(await ItemAsync(db, proof.Id, cancellationToken));
    }

    /// <summary>Historical payment fields: amount, a past date within the retention period, optional texts within their lengths, no duplicate operation.</summary>
    private static async Task<IResult?> ValidateHistoricalAsync(
        AppDbContext db, CondoClock clock, decimal amount, DateOnly operationDate, string? payerName, string? bankName, string? operationNumber,
        string? paperReceiptNumber, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalizedNumber = PaymentProof.NormalizeOperationNumber(operationNumber ?? "");
        if (amount <= 0 || amount > 99_999 || operationDate > clock.Today
            || operationDate < clock.Today.AddMonths(-CondoProfile.Current.FileRetentionMonths)
            || (payerName?.Trim().Length ?? 0) > 120 || (bankName?.Trim().Length ?? 0) > 80 || normalizedNumber.Length > 40
            || (paperReceiptNumber?.Trim().Length ?? 0) > 40)
        {
            return ApiProblems.BadRequest(ErrorCodes.ValidationFailed);
        }

        return normalizedNumber.Length > 0
               && await IsDuplicateAsync(db, (bankName ?? "").Trim(), normalizedNumber, cancellationToken, excludeId)
            ? ApiProblems.Conflict(ErrorCodes.DuplicateOperationNumber)
            : null;
    }

    private static string HistoricalSnapshot(PaymentProof proof) => AuditLog.Json(new
    {
        proof.DeclaredAmount, proof.OperationDate, proof.PayerName, proof.BankName, proof.OperationNumber, proof.PaperReceiptNumber,
        proof.HasImage,
    });

    /// <summary>Names to suggest as "Recibí de" when an admin registers a payment.</summary>
    private static async Task<IResult> ResidentsAsync(Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.Users.AsNoTracking()
            .Where(u => u.ApartmentId == id && u.IsActive)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => u.FirstName + " " + u.LastName)
            .ToListAsync(cancellationToken));

    /// <summary>The receipt PDF itself (to share it, e.g. by WhatsApp from the phone).</summary>
    private static async Task<IResult> ReceiptPdfAsync(
        Guid id, ClaimsPrincipal principal, AppDbContext db, IFileStorage storage, CancellationToken cancellationToken)
    {
        var proof = await db.PaymentProofs.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (proof is null || !await CanViewProofAsync(db, principal, proof.ApartmentId, cancellationToken))
        {
            return ApiProblems.NotFound();
        }

        var receipt = await db.Receipts.AsNoTracking().Where(r => r.PaymentProofId == id)
            .Select(r => new { r.Number, r.PdfObjectKey }).SingleOrDefaultAsync(cancellationToken);
        var pdf = receipt?.PdfObjectKey is { } key ? await storage.GetAsync(key, cancellationToken) : null;
        return pdf is null
            ? ApiProblems.NotFound()
            : TypedResults.File(pdf, "application/pdf", LedgerService.ReceiptFileName(receipt!.Number));
    }

    /// <summary>Approves the saved proof's effects: FIFO allocation, gap-free receipt with its PDF, notice. Caller owns the transaction.</summary>
    private static async Task<Receipt> SettleAndIssueReceiptAsync(
        AppDbContext db, LedgerService ledger, NotificationService notifications, PaymentProof proof, AuditSource source, Guid adminId,
        DateTimeOffset now, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await ledger.SettleAsync(proof.ApartmentId, source, adminId, ct);
        await db.SaveChangesAsync(ct);

        var sequence = await ledger.NextReceiptSequenceAsync(ct);
        var receipt = new Receipt(proof.Id, Period.FromDate(DateOnly.FromDateTime(CondoClock.ToLocal(now).DateTime)).Value, sequence,
            LedgerService.ReceiptNumber(sequence), now);
        await ledger.StoreReceiptPdfAsync(receipt, proof, voided: false, ct);
        db.Receipts.Add(receipt);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.Receipt, receipt.Id.ToString(), "Issued", source, adminId, now,
            newValues: AuditLog.Json(new { receipt.Number, receipt.FolioPeriod, PaymentProofId = proof.Id }), apartmentId: proof.ApartmentId));
        await notifications.NotifyApartmentAsync(proof.ApartmentId, NotificationType.PaymentApproved,
            new PaymentNoticeData(proof.Id, proof.ApprovedAmount!.Value, receipt.Number, null), ct);
        await db.SaveChangesAsync(ct);
        return receipt;
    }

    /// <summary>Amount, date (not future, at most a year old), bank and operation number; then the duplicate check.</summary>
    private static async Task<IResult?> ValidateProofAsync(
        AppDbContext db, CondoClock clock, decimal amount, DateOnly operationDate, string bankName, string operationNumber,
        CancellationToken cancellationToken)
    {
        var normalizedNumber = PaymentProof.NormalizeOperationNumber(operationNumber);
        if (amount <= 0 || amount > 99_999 || string.IsNullOrWhiteSpace(bankName) || bankName.Length > 80
            || normalizedNumber.Length is 0 or > 40 || operationDate > clock.Today || operationDate < clock.Today.AddYears(-1))
        {
            return ApiProblems.BadRequest(ErrorCodes.ValidationFailed);
        }

        return await IsDuplicateAsync(db, bankName.Trim(), normalizedNumber, cancellationToken)
            ? ApiProblems.Conflict(ErrorCodes.DuplicateOperationNumber)
            : null;
    }

    /// <summary>Validates and re-encodes the image and stores it; null when it is not a usable image.</summary>
    private static async Task<string?> StoreImageAsync(IFormFile image, Guid apartmentId, IFileStorage storage, CancellationToken cancellationToken)
    {
        var jpeg = await ReadImageAsync(image, cancellationToken);
        if (jpeg is null)
        {
            return null;
        }

        var key = StorageKeys.PaymentProof(apartmentId, Guid.CreateVersion7());
        using var content = new MemoryStream(jpeg);
        await storage.PutAsync(key, content, "image/jpeg", cancellationToken);
        return key;
    }

    /// <summary>The upload re-encoded as JPEG ≤1600 px without metadata; null when it is not a usable image.</summary>
    private static async Task<byte[]?> ReadImageAsync(IFormFile image, CancellationToken cancellationToken)
    {
        if (image.Length is 0 or > ProofImage.MaxUploadBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, cancellationToken);
        return ProofImage.Normalize(buffer.ToArray());
    }

    /// <summary>
    /// Not found, an admin without an apartment, the reviewer's own apartment, a pending proof the reviewer sent
    /// (someone else must approve or reject it; voiding one's own registration is allowed), stale or wrong status.
    /// </summary>
    private static async Task<IResult?> ReviewProblemAsync(
        AppDbContext db, ClaimsPrincipal principal, PaymentProof? proof, PaymentProofStatus expected, int? rowVersion,
        CancellationToken cancellationToken)
    {
        if (proof is null)
        {
            return ApiProblems.NotFound();
        }

        if (await StaffProblemAsync(db, principal, proof.ApartmentId, cancellationToken) is { } staffProblem)
        {
            return staffProblem;
        }

        if (expected == PaymentProofStatus.Pending && proof.SubmittedById == principal.GetUserId())
        {
            return ApiProblems.Forbidden(ErrorCodes.OwnSubmissionReview);
        }

        // Another admin changed it since this one loaded the queue (also caught by xmin if it happens right now).
        if (rowVersion is not null && unchecked((uint)rowVersion.Value) != db.Entry(proof).Property<uint>("RowVersion").CurrentValue)
        {
            return ApiProblems.Conflict(ErrorCodes.Conflict);
        }

        if (proof.Status != expected)
        {
            return ApiProblems.Conflict(expected == PaymentProofStatus.Pending ? ErrorCodes.ProofNotPending : ErrorCodes.ProofNotApproved);
        }

        return null;
    }

    /// <summary>
    /// Admins act on payments only once they have an apartment, and never on their own apartment. The gatehouse has
    /// no apartment, so neither rule applies to it.
    /// </summary>
    private static async Task<IResult?> StaffProblemAsync(AppDbContext db, ClaimsPrincipal principal, Guid apartmentId, CancellationToken cancellationToken)
    {
        if (!principal.IsAdmin())
        {
            return null;
        }

        var userId = principal.GetUserId();
        var ownApartment = await db.Users.Where(u => u.Id == userId).Select(u => u.ApartmentId).SingleAsync(cancellationToken);
        return ownApartment is null ? ApiProblems.Forbidden(ErrorCodes.AdminApartmentRequired)
            : ownApartment == apartmentId ? ApiProblems.Forbidden(ErrorCodes.OwnApartmentReview)
            : null;
    }

    /// <summary>Proof images and receipts: the apartment's residents, and staff for any apartment (they review them).</summary>
    private static async Task<bool> CanViewProofAsync(AppDbContext db, ClaimsPrincipal principal, Guid apartmentId, CancellationToken cancellationToken) =>
        principal.IsStaff() || await ApartmentAccess.CanAccessAsync(db, principal, apartmentId, cancellationToken);

    private static Task<bool> IsDuplicateAsync(
        AppDbContext db, string bankName, string operationNumber, CancellationToken cancellationToken, Guid? excludeId = null) =>
        db.PaymentProofs.AnyAsync(
            p => p.BankName == bankName && p.OperationNumber == operationNumber && p.Status != PaymentProofStatus.Rejected && p.Id != excludeId,
            cancellationToken);

    private static async Task<PaymentProofItem> ItemAsync(AppDbContext db, Guid id, CancellationToken cancellationToken) =>
        (await ItemsAsync(db, db.PaymentProofs.Where(p => p.Id == id), cancellationToken)).Single();

    private static async Task<List<PaymentProofItem>> ItemsAsync(AppDbContext db, IQueryable<PaymentProof> source, CancellationToken cancellationToken)
    {
        var rows = await (
                from p in source.AsNoTracking()
                join a in db.Apartments on p.ApartmentId equals a.Id
                join s in db.Users on p.SubmittedById equals s.Id
                join r in db.Users on p.ReviewedById equals r.Id into reviewers
                from r in reviewers.DefaultIfEmpty()
                join rc in db.Receipts on p.Id equals rc.PaymentProofId into receipts
                from rc in receipts.DefaultIfEmpty()
                orderby p.SubmittedAt descending
                select new
                {
                    Proof = p,
                    ApartmentCode = a.Code,
                    Submitter = s.FirstName + " " + s.LastName,
                    Reviewer = r == null ? null : r.FirstName + " " + r.LastName,
                    ReceiptNumber = rc == null ? null : rc.Number,
                    RowVersion = EF.Property<uint>(p, "RowVersion"),
                })
            .ToListAsync(cancellationToken);

        // Months each approved payment pays now, and what is left of it as credit for the next charges.
        var proofIds = rows.Select(x => x.Proof.Id).ToList();
        var applied = (await (
                    from al in db.PaymentAllocations.AsNoTracking()
                    where proofIds.Contains(al.PaymentProofId) && al.ReversedAt == null
                    join c in db.Charges on al.ChargeId equals c.Id
                    select new { al.PaymentProofId, c.Period, al.Amount })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.PaymentProofId)
            .ToDictionary(g => g.Key, g => (Periods: (IReadOnlyList<int>)g.Select(x => x.Period).Distinct().Order().ToList(), Amount: g.Sum(x => x.Amount)));

        return rows.Select(x => new PaymentProofItem(
                x.Proof.Id, x.Proof.ApartmentId, x.ApartmentCode, x.Submitter, x.Proof.DeclaredAmount, x.Proof.ApprovedAmount,
                x.Proof.OperationDate, x.Proof.BankName, x.Proof.OperationNumber, (ProofStatus)(int)x.Proof.Status, x.Proof.SubmittedAt,
                x.Proof.ReviewedAt, x.Reviewer, x.Proof.RejectionReason, x.Proof.VoidReason, x.ReceiptNumber, unchecked((int)x.RowVersion),
                x.Proof.PayerName, x.Proof.HasImage, x.Proof.PaperReceiptNumber,
                applied.TryGetValue(x.Proof.Id, out var a) ? a.Periods : [],
                x.Proof.Status == PaymentProofStatus.Approved ? (x.Proof.ApprovedAmount ?? 0) - (applied.TryGetValue(x.Proof.Id, out var b) ? b.Amount : 0) : 0))
            .ToList();
    }
}
