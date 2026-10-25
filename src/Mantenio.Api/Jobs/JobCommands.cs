using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Push;
using Mantenio.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Jobs;

/// <summary>
/// <c>Mantenio.Api job &lt;name&gt;</c>: runs one scheduled job and exits (Container Apps Jobs; cron in UTC):
/// <list type="bullet">
///   <item><c>generate-charges</c>: day 1, 00:05 Lima (<c>5 5 1 * *</c>).</item>
///   <item><c>late-fees</c>: daily, 00:30 Lima (<c>30 5 * * *</c>).</item>
///   <item><c>reminders</c>: day 15, 09:00 Lima (<c>0 14 15 * *</c>).</item>
///   <item><c>cleanup</c>: daily, 03:30 Lima (<c>30 8 * * *</c>).</item>
/// </list>
/// The nightly database backup is a separate image (<c>deploy/backup</c>), because it needs <c>pg_dump</c>.
/// </summary>
internal static class JobCommands
{
    public static readonly string[] Names = ["generate-charges", "late-fees", "reminders", "cleanup"];

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
        }

        // Push the notices this job created before the process exits.
        var pushed = await services.GetRequiredService<PushDispatcher>().DispatchPendingAsync(cancellationToken);
        await output.WriteLineAsync($"Push messages sent: {pushed}.");
        return 0;
    }

    /// <summary>
    /// Removes data that only matters for a while: expired or revoked refresh tokens and unused, expired invitation
    /// codes (after 30 days). Ledger rows are never touched.
    /// </summary>
    public static async Task<string> CleanupAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var cutoff = now.AddDays(-30);
        var tokens = await db.RefreshTokens.Where(t => t.ExpiresAt < cutoff || t.RevokedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.InvitationCodes.Where(i => i.UsedAt == null && i.ExpiresAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.Job, "cleanup", "Cleanup", AuditSource.System, null, now,
            newValues: AuditLog.Json(new { RefreshTokens = tokens, InvitationCodes = invitations })));
        await db.SaveChangesAsync(cancellationToken);
        return $"Removed {tokens} refresh tokens and {invitations} invitation codes.";
    }
}
