namespace Mantenio.Shared.Apartments;

/// <summary>An apartment as shown in pickers, e.g. Code "H-102".</summary>
public sealed record ApartmentOption(Guid Id, string Code, string Tower, string Number);

public sealed record ApartmentResident(Guid UserId, string FirstName, string LastName);

public sealed record ApartmentDetails(Guid Id, string Code, string Tower, string Number, IReadOnlyList<ApartmentResident> Residents);
