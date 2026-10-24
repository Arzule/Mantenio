using Mantenio.Shared.Apartments;

namespace Mantenio.UI.Components;

public sealed record UserActionResult(string Reason, ApartmentOption? Apartment);
