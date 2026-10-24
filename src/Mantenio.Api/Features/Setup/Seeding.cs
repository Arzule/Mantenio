using System.Security.Cryptography;
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
        var entries = ApartmentList.Parse(csv);
        var towers = await db.Towers.Include(t => t.Apartments).ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var nextSortOrder = towers.Count == 0 ? 1 : towers.Values.Max(t => t.SortOrder) + 1;
        var added = 0;

        foreach (var entry in entries)
        {
            if (!towers.TryGetValue(entry.Tower, out var tower))
            {
                tower = new Tower(entry.Tower, nextSortOrder++);
                towers[tower.Name] = tower;
                db.Towers.Add(tower);
            }

            var code = Apartment.BuildCode(entry.Tower, entry.Number);
            if (tower.Apartments.Any(a => a.Code == code))
            {
                continue;
            }

            var apartment = new Apartment(tower, entry.Number);
            tower.Apartments.Add(apartment);
            db.Apartments.Add(apartment);
            added++;
        }

        if (added > 0)
        {
            db.AuditEvents.Add(new AuditEvent(
                AuditEntities.ApartmentList, "seed", "Seeded", AuditSource.System, null, time.GetUtcNow(),
                newValues: AuditLog.Json(new { Towers = towers.Count, ApartmentsAdded = added })));
            await db.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    /// <summary>
    /// Creates the founder admin. Without a password a random temporary one is generated and must be changed at first login.
    /// Returns null when a founder already exists.
    /// </summary>
    public async Task<(AppUser User, string Password)?> CreateFounderAsync(
        string email, string firstName, string lastName, string? password, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(u => u.IsFounder, cancellationToken))
        {
            return null;
        }

        var now = time.GetUtcNow();
        var user = AppUser.Create(email, firstName, lastName, UserRole.Admin, now);
        user.IsFounder = true;
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
            newValues: AuditLog.Json(new { user.Email, user.FirstName, user.LastName })));
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
