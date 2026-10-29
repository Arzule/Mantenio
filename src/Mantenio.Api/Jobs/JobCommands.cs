using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Push;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Domain.Audit;
using Mantenio.Shared.Condos;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Jobs;

/// <summary>
/// <c>Mantenio.Api job &lt;name&gt;</c>: runs one scheduled job and exits (Container Apps Jobs; cron in UTC):
/// <list type="bullet">
///   <item><c>generate-charges</c>: day 1, 00:05 Lima (<c>5 5 1 * *</c>).</item>
///   <item><c>late-fees</c>: daily, 00:30 Lima (<c>30 5 * * *</c>).</item>
///   <item><c>reminders</c>: day 15, 09:00 Lima (<c>0 14 15 * *</c>).</item>
///   <item><c>cleanup</c>: daily, 03:30 Lima (<c>30 8 * * *</c>).</item>
///   <item><c>board-summary</c>: day 1, 06:30 Lima (<c>30 11 1 * *</c>).</item>
/// </list>
/// The nightly database backup is a separate image (<c>deploy/backup</c>), because it needs <c>pg_dump</c>.
/// </summary>
internal static class JobCommands
{
    public static readonly string[] Names = ["generate-charges", "late-fees", "reminders", "cleanup", "board-summary"];

    public static async Task<int> RunAsync(IServiceProvider rootServices, string? name, CancellationToken cancellationToken)
    {
        if (name is null || !Names.Contains(name))
        {
            await Console.Error.WriteLineAsync($"Usage: job <{string.Join('|', Names)}>");
            return 1;
        }

        await using var scope = rootServices.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var jobs = services.GetRequiredService<LedgerJobs>();
        var output = Console.Out;
        switch (name)
        {
            case "generate-charges":
                var result = await jobs.GenerateChargesAsync(cancellationToken);
                await output.WriteLineAsync($"Period {result.Period}: {result.Created} created, {result.AlreadyExisting} already there, {result.SkippedFrozen} frozen.");
                break;
            case "late-fees":
                await output.WriteLineAsync($"Late fees created: {await jobs.ApplyLateFeesAsync(cancellationToken)}.");
                break;
            case "reminders":
                await output.WriteLineAsync($"Apartments reminded: {await jobs.SendDebtRemindersAsync(cancellationToken)}.");
                break;
            case "cleanup":
                await output.WriteLineAsync(await CleanupAsync(services, cancellationToken));
                break;
            case "board-summary":
                await output.WriteLineAsync($"Board members emailed: {await services.GetRequiredService<BoardSummaryJob>().SendAsync(cancellationToken)}.");
                break;
        }

        // Push the notices this job created before the process exits.
        var pushed = await services.GetRequiredService<PushDispatcher>().DispatchPendingAsync(cancellationToken);
        await output.WriteLineAsync($"Push messages sent: {pushed}.");
        return 0;
    }

    /// <summary>
    /// Removes data that only matters for a while: expired or revoked refresh tokens and unused, expired invitation
    /// codes (after 30 days), and the files past the retention period (see <see cref="PurgeExpiredFilesAsync"/>).
    /// Ledger rows are never touched.
    /// </summary>
    public static async Task<string> CleanupAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var cutoff = now.AddDays(-30);
        var tokens = await db.RefreshTokens.Where(t => t.ExpiresAt < cutoff || t.RevokedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.InvitationCodes.Where(i => i.UsedAt == null && i.ExpiresAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        var (images, pdfs) = await PurgeExpiredFilesAsync(
            db, services.GetRequiredService<IFileStorage>(), services.GetRequiredService<CondoClock>().Today, cancellationToken);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.Job, "cleanup", "Cleanup", AuditSource.System, null, now,
            newValues: AuditLog.Json(new { RefreshTokens = tokens, InvitationCodes = invitations, ProofImages = images, ReceiptPdfs = pdfs })));
        await db.SaveChangesAsync(cancellationToken);
        return $"Removed {tokens} refresh tokens, {invitations} invitation codes, {images} proof images and {pdfs} receipt PDFs.";
    }

    /// <summary>
    /// Retention (condo profile FileRetentionMonths, 10 years by default): deletes the photos and receipt PDFs of
    /// payments made before the cutoff, so file storage stays within the free tier. The rows stay; they just no
    /// longer have a file. At most 500 of each per run. The caller saves.
    /// </summary>
    public static async Task<(int Images, int Pdfs)> PurgeExpiredFilesAsync(
        AppDbContext db, IFileStorage storage, DateOnly today, CancellationToken cancellationToken)
    {
        var cutoff = today.AddMonths(-CondoProfile.Current.FileRetentionMonths);
        var proofs = await db.PaymentProofs.Where(p => p.OperationDate < cutoff && p.ImageObjectKey != "").Take(500).ToListAsync(cancellationToken);
        foreach (var proof in proofs)
        {
            await storage.DeleteAsync(proof.ImageObjectKey, cancellationToken);
            proof.RemoveImage();
        }

        var receipts = await (
                from r in db.Receipts
                join p in db.PaymentProofs on r.PaymentProofId equals p.Id
                where p.OperationDate < cutoff && r.PdfObjectKey != null
                select r)
            .Take(500).ToListAsync(cancellationToken);
        foreach (var receipt in receipts)
        {
            // Both versions: the original PDF and the VOIDED copy, if the payment was voided.
            await storage.DeleteAsync(StorageKeys.Receipt(receipt.Number, voided: false), cancellationToken);
            await storage.DeleteAsync(StorageKeys.Receipt(receipt.Number, voided: true), cancellationToken);
            receipt.RemovePdf();
        }

        return (proofs.Count, receipts.Count);
    }
}
