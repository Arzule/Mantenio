using Mantenio.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Mantenio.Api.Infrastructure.Auth;

/// <summary>
/// Rejects access tokens whose session was revoked (logout, password change, deactivation) without waiting for the
/// token to expire. A session is alive while its token family has an unrevoked refresh token. Live sessions are
/// cached briefly so most requests skip the database; revocations on this instance drop the cache at once.
/// </summary>
public sealed class SessionGuard(IServiceScopeFactory scopes, IMemoryCache cache, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(Key(sessionId), out _))
        {
            return true;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var active = await db.RefreshTokens.AnyAsync(
            t => t.FamilyId == sessionId && t.RevokedAt == null && t.ExpiresAt > now, cancellationToken);
        if (active)
        {
            cache.Set(Key(sessionId), true, CacheFor);
        }

        return active;
    }

    public void Forget(IEnumerable<Guid> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        foreach (var id in sessionIds)
        {
            cache.Remove(Key(id));
        }
    }

    private static string Key(Guid sessionId) => $"session:{sessionId:N}";
}
