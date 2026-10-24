namespace Mantenio.Domain.Condo;

/// <summary>A building of the condo ("Edificio"/"Torre" in the UI, depending on the condo profile).</summary>
public sealed class Tower
{
    private Tower() { } // EF Core

    public Tower(string name, int sortOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = Guid.CreateVersion7();
        Name = name.Trim();
        SortOrder = sortOrder;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public int SortOrder { get; private set; }

    public List<Apartment> Apartments { get; private set; } = [];
}
