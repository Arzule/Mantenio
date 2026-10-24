namespace Mantenio.Domain.Condo;

public sealed class Apartment
{
    private Apartment() { } // EF Core

    public Apartment(Tower tower, string number)
    {
        ArgumentNullException.ThrowIfNull(tower);
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        Id = Guid.CreateVersion7();
        TowerId = tower.Id;
        Tower = tower;
        Number = number.Trim();
        Code = BuildCode(tower.Name, Number);
    }

    public Guid Id { get; private set; }
    public Guid TowerId { get; private set; }
    public Tower? Tower { get; private set; }
    public string Number { get; private set; } = "";

    /// <summary>Unique, human-friendly identifier used in search and CSV imports, e.g. "H-102".</summary>
    public string Code { get; private set; } = "";

    /// <summary>
    /// Frozen apartments get no new charges, late fees or reminders (e.g. under a legal process). Previous debt stays.
    /// The history is in <see cref="ApartmentFreeze"/>.
    /// </summary>
    public bool IsFrozen { get; private set; }

    public ApartmentFreeze Freeze(string reason, Guid adminId, DateTimeOffset now)
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException("The apartment is already frozen.");
        }

        IsFrozen = true;
        return new ApartmentFreeze(Id, reason, adminId, now);
    }

    public void Unfreeze(ApartmentFreeze activeFreeze, string reason, Guid adminId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activeFreeze);
        if (!IsFrozen || activeFreeze.ApartmentId != Id)
        {
            throw new InvalidOperationException("The apartment is not frozen.");
        }

        activeFreeze.End(reason, adminId, now);
        IsFrozen = false;
    }

    public static string BuildCode(string towerName, string number) =>
        $"{towerName.Trim().ToUpperInvariant()}-{number.Trim().ToUpperInvariant()}";
}
