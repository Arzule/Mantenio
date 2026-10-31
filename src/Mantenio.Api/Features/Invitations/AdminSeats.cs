using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Condo;
using Mantenio.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Invitations;

/// <summary>Seat limits for staff accounts: admins (<see cref="CondoSettings.MaxAdmins"/>) and the gatehouse (<see cref="CondoSettings.MaxGuards"/>).</summary>
internal static class AdminSeats
{
    /// <summary>Arbitrary constant identifying the "staff seats" advisory lock.</summary>
    private const long LockKey = 0x4D414E54_41444D;

    /// <summary>
    /// True when one more active account of <paramref name="role"/> fits under its limit (residents have none). Must
    /// run inside a transaction: it takes an advisory lock so two sign-ups at the same moment cannot both take the last seat.
    /// </summary>
    public static async Task<bool> HasSeatAsync(AppDbContext db, UserRole role, CancellationToken cancellationToken)
    {
        if (role == UserRole.Resident)
        {
            return true;
        }

        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
        return await AnySeatLeftAsync(db, role, cancellationToken);
    }

    /// <summary>Quick check without locking, to fail early before generating a staff code.</summary>
    public static async Task<bool> AnySeatLeftAsync(AppDbContext db, UserRole role, CancellationToken cancellationToken)
    {
        if (role == UserRole.Resident)
        {
            return true;
        }

        var active = await db.Users.CountAsync(u => u.Role == role && u.IsActive, cancellationToken);
        if (role == UserRole.Guard)
        {
            return active < CondoSettings.MaxGuards;
        }

        var settings = await db.CondoSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return settings?.HasAdminSeat(active) ?? active < CondoSettings.DefaultMaxAdmins;
    }
}
