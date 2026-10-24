using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Condo;
using Mantenio.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Invitations;

internal static class AdminSeats
{
    /// <summary>Arbitrary constant identifying the "admin seats" advisory lock.</summary>
    private const long LockKey = 0x4D414E54_41444D;

    /// <summary>
    /// True when one more active admin fits under <see cref="CondoSettings.MaxAdmins"/>. Must run inside a transaction:
    /// it takes an advisory lock so two admin sign-ups at the same moment cannot both take the last seat.
    /// </summary>
    public static async Task<bool> HasSeatAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
        var settings = await db.CondoSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var activeAdmins = await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive, cancellationToken);
        return settings?.HasAdminSeat(activeAdmins) ?? activeAdmins < CondoSettings.DefaultMaxAdmins;
    }

    /// <summary>Quick check without locking, to fail early before generating an admin code.</summary>
    public static async Task<bool> AnySeatLeftAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.CondoSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var activeAdmins = await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive, cancellationToken);
        return settings?.HasAdminSeat(activeAdmins) ?? activeAdmins < CondoSettings.DefaultMaxAdmins;
    }
}
