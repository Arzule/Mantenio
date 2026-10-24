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

    /// <summary>Apartment-specific fee; null means the global fee applies.</summary>
    public decimal? FeeOverride { get; private set; }

    public bool IsFrozen { get; private set; }

    public static string BuildCode(string towerName, string number) =>
        $"{towerName.Trim().ToUpperInvariant()}-{number.Trim().ToUpperInvariant()}";
}
