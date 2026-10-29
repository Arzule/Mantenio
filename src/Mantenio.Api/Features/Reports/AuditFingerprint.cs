using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mantenio.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Reports;

/// <summary>How many audit events existed at a moment and a SHA-256 over all of them.</summary>
public sealed record AuditFingerprintResult(int Events, string Hash);

/// <summary>
/// A fingerprint of the whole audit log up to a moment: if anyone later deleted or edited an older event, recomputing
/// it for the same moment gives a different hash. It travels in the monthly email to the board and in the period
/// report, so the copies kept by several people can be compared.
/// </summary>
internal static class AuditFingerprint
{
    public static async Task<AuditFingerprintResult> ComputeAsync(AppDbContext db, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        await foreach (var e in db.AuditEvents.AsNoTracking()
                           .Where(e => e.CreatedAt <= until)
                           .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            var line = string.Join('|', e.Id, e.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), e.EntityType, e.EntityId, e.Action,
                e.Source, e.ActorUserId, e.ApartmentId, e.ChargeId, e.OldValues, e.NewValues, e.Reason) + "\n";
            sha.AppendData(Encoding.UTF8.GetBytes(line));
            count++;
        }

        return new AuditFingerprintResult(count, Convert.ToHexString(sha.GetHashAndReset()));
    }
}
