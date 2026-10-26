namespace Mantenio.Shared.Apartments;

/// <summary>An apartment as shown in pickers, e.g. Code "H-102".</summary>
public sealed record ApartmentOption(Guid Id, string Code, string Tower, string Number);

public static class ApartmentOptionLimits
{
    /// <summary>The most options one call returns: enough for every apartment of a condo.</summary>
    public const int Max = 1000;
}

public sealed record ApartmentResident(Guid UserId, string FirstName, string LastName);

public sealed record ApartmentDetails(Guid Id, string Code, string Tower, string Number, IReadOnlyList<ApartmentResident> Residents);
