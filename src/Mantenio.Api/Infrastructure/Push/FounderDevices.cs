using System.Text.Json;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Infrastructure.Push;

/// <summary>
/// A copy of the founder's push tokens in file storage (R2), outside the database: when the database is down, the
/// outage alert still reaches the founder's phone. Rewritten whenever the founder registers or removes a phone.
/// </summary>
internal sealed class FounderDevices(AppDbContext db, IFileStorage storage)
{
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var tokens = await db.UserDevices
            .Where(d => db.Users.Any(u => u.Id == d.UserId && u.IsFounder && u.IsActive))
            .Select(d => d.Token)
            .ToListAsync(cancellationToken);
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(tokens));
        await storage.PutAsync(StorageKeys.FounderDevices, content, "application/json", cancellationToken);
    }

    /// <summary>The last saved tokens; empty when there is no copy yet. Does not touch the database.</summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(IFileStorage storage, CancellationToken cancellationToken)
    {
        var data = await storage.GetAsync(StorageKeys.FounderDevices, cancellationToken);
        return data is null ? [] : JsonSerializer.Deserialize<List<string>>(data) ?? [];
    }
}
