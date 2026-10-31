using System.Security.Cryptography;
using Mantenio.Api.Features.Apartments;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Condo;
using Mantenio.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Setup;

/// <summary>One-time setup: the condo structure and the founder admin. Both are idempotent.</summary>
internal sealed class Seeding(AppDbContext db, UserManager<AppUser> users, TimeProvider time)
{
    /// <summary>The apartment list compiled from <c>condos/&lt;slug&gt;/apartments.csv</c>, if the profile has one.</summary>
    public static string? EmbeddedApartmentList()
    {
        using var stream = typeof(Seeding).Assembly.GetManifestResourceStream("apartments.csv");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Adds the towers and apartments that do not exist yet. Returns how many apartments were added.</summary>
    public async Task<int> SeedApartmentsAsync(string csv, CancellationToken cancellationToken)
    {
        var result = await ApartmentImport.ApplyAsync(
            db, ApartmentList.Parse(csv), AuditSource.System, null, time.GetUtcNow(), cancellationToken);
        return result.ApartmentsAdded;
    }

    /// <summary>
    /// Creates the founder admin (superadmin). Without a password a random temporary one is generated and must be
    /// changed at first login. Without <paramref name="apartmentCode"/> the founder assigns their own apartment later
    /// (Users), before reviewing payments. Returns null when a founder already exists.
    /// </summary>
    public async Task<(AppUser User, string Password)?> CreateFounderAsync(
        string email, string firstName, string lastName, string? password, CancellationToken cancellationToken, string? apartmentCode = null)
    {
        if (await db.Users.AnyAsync(u => u.IsFounder, cancellationToken))
        {
            return null;
        }

        var now = time.GetUtcNow();
        var user = AppUser.Create(email, firstName, lastName, UserRole.Admin, now);
        user.IsFounder = true;
        if (apartmentCode is { Length: > 0 })
        {
            user.ApartmentId = await db.Apartments.Where(a => a.Code == apartmentCode).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Apartment {apartmentCode} does not exist.");
        }

        user.EmailConfirmed = true; // every admin login still needs the emailed code
        user.MustChangePassword = password is null;
        var finalPassword = password ?? TemporaryPassword();

        var result = await users.CreateAsync(user, finalPassword);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not create the founder: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        db.AuditEvents.Add(new AuditEvent(
            AuditEntities.User, user.Id.ToString(), "FounderSeeded", AuditSource.System, null, now,
            newValues: AuditLog.Json(new { user.Email, user.FirstName, user.LastName, user.ApartmentId }),
            apartmentId: user.ApartmentId));
        await db.SaveChangesAsync(cancellationToken);
        return (user, finalPassword);
    }

    /// <summary>12 characters without look-alikes (0/O, 1/l), always with a digit.</summary>
    private static string TemporaryPassword()
    {
        const string letters = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ";
        const string digits = "23456789";
        var chars = RandomNumberGenerator.GetItems<char>(letters + digits, 10).ToList();
        chars.AddRange(RandomNumberGenerator.GetItems<char>(digits, 2));
        RandomNumberGenerator.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chars));
        return new string([.. chars]);
    }
}
