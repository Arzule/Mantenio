using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Identity;
using Mantenio.Shared.Auth;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Infrastructure.Auth;

/// <summary>Issues, rotates and revokes refresh-token sessions.</summary>
public sealed class SessionService(AppDbContext db, TokenService tokens, SessionGuard guard, TimeProvider time)
{
    /// <summary>Starts a new session (a new token family) for a user who just proved who they are.</summary>
    public async Task<AuthTokens> StartAsync(AppUser user, bool keepSignedIn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        await db.Entry(user).Reference(u => u.Apartment).LoadAsync(cancellationToken);
        var (refreshToken, entity) = NewRefreshToken(user, Guid.CreateVersion7(), keepSignedIn);
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return Tokens(user, refreshToken, entity);
    }

    /// <summary>
    /// Rotates a refresh token. Returns null when it is unknown, expired, revoked or the account is inactive.
    /// Presenting an already-rotated token means it was stolen or replayed: the whole family is revoked.
    /// </summary>
    public async Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var hash = TokenService.HashRefreshToken(refreshToken);
        var current = await db.RefreshTokens
            .Include(t => t.User!).ThenInclude(u => u.Apartment)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (current?.User is null)
        {
            return null;
        }

        if (current.RevokedAt is not null)
        {
            if (current.ReplacedById is not null)
            {
                await RevokeFamilyAsync(current.FamilyId, cancellationToken);
            }

            return null;
        }

        if (current.ExpiresAt <= now || !current.User.IsActive)
        {
            current.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var (newToken, replacement) = NewRefreshToken(current.User, current.FamilyId, current.KeepSignedIn);
        current.RevokedAt = now;
        current.ReplacedById = replacement.Id;
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(cancellationToken);
        return Tokens(current.User, newToken, replacement);
    }

    /// <summary>Logout: revokes every token of the session the given refresh token belongs to.</summary>
    public async Task SignOutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = TokenService.HashRefreshToken(refreshToken);
        var familyId = await db.RefreshTokens
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.FamilyId)
            .SingleOrDefaultAsync(cancellationToken);
        if (familyId is { } id)
        {
            await RevokeFamilyAsync(id, cancellationToken);
        }
    }

    /// <summary>Signs the user out everywhere (deactivation, password change or reset).</summary>
    public async Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var live = db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null);
        var families = await live.Select(t => t.FamilyId).Distinct().ToListAsync(cancellationToken);
        var revoked = await live.ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
        guard.Forget(families);
        return revoked;
    }

    private async Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var revoked = await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
        guard.Forget([familyId]);
        return revoked;
    }

    private (string Token, RefreshToken Entity) NewRefreshToken(AppUser user, Guid familyId, bool keepSignedIn)
    {
        var now = time.GetUtcNow();
        var token = TokenService.NewRefreshToken();
        var entity = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.HashRefreshToken(token),
            FamilyId = familyId,
            KeepSignedIn = keepSignedIn,
            CreatedAt = now,
            ExpiresAt = now + SessionPolicy.RefreshTokenLifetime(user.Role, keepSignedIn),
        };
        return (token, entity);
    }

    private AuthTokens Tokens(AppUser user, string refreshToken, RefreshToken entity)
    {
        var (accessToken, accessExpiresAt) = tokens.CreateAccessToken(user, entity.FamilyId);
        return new AuthTokens(accessToken, accessExpiresAt, refreshToken, entity.ExpiresAt);
    }
}
