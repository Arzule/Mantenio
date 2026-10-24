using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Condo;
using Mantenio.Shared.Apartments;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Apartments;

/// <summary>
/// Adds towers and apartments from a list (CSV <c>tower,number</c>). Existing apartments are skipped, never changed,
/// so importing the same file twice is harmless. Used by the admin import and by the seeding command.
/// </summary>
internal static class ApartmentImport
{
    public static async Task<ImportApartmentsPreview> PreviewAsync(AppDbContext db, string csv, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApartmentListEntry> entries;
        try
        {
            entries = ApartmentList.Parse(csv);
        }
        catch (FormatException ex)
        {
            return new ImportApartmentsPreview([], [], 0, ex.Message);
        }

        var towers = await db.Towers.Select(t => t.Name).ToListAsync(cancellationToken);
        var codes = (await db.Apartments.Select(a => a.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var newTowers = entries.Select(e => e.Tower.Trim())
            .Where(name => !towers.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var newApartments = entries.Select(e => Apartment.BuildCode(e.Tower, e.Number)).Where(code => !codes.Contains(code)).ToList();
        return new ImportApartmentsPreview(newTowers, newApartments, entries.Count - newApartments.Count, null);
    }

    public static async Task<ImportApartmentsResult> ApplyAsync(
        AppDbContext db, IReadOnlyList<ApartmentListEntry> entries, AuditSource source, Guid? actorId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var towers = await db.Towers.Include(t => t.Apartments).ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var nextSortOrder = towers.Count == 0 ? 1 : towers.Values.Max(t => t.SortOrder) + 1;
        var towersAdded = 0;
        var apartmentsAdded = 0;

        foreach (var entry in entries)
        {
            if (!towers.TryGetValue(entry.Tower.Trim(), out var tower))
            {
                tower = new Tower(entry.Tower, nextSortOrder++);
                towers[tower.Name] = tower;
                db.Towers.Add(tower);
                towersAdded++;
            }

            var code = Apartment.BuildCode(entry.Tower, entry.Number);
            if (tower.Apartments.Any(a => a.Code == code))
            {
                continue;
            }

            var apartment = new Apartment(tower, entry.Number);
            tower.Apartments.Add(apartment);
            db.Apartments.Add(apartment);
            apartmentsAdded++;
        }

        if (apartmentsAdded > 0 || towersAdded > 0)
        {
            db.AuditEvents.Add(new AuditEvent(
                AuditEntities.ApartmentList, "import", "Imported", source, actorId, now,
                newValues: AuditLog.Json(new { TowersAdded = towersAdded, ApartmentsAdded = apartmentsAdded })));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ImportApartmentsResult(towersAdded, apartmentsAdded);
    }
}
