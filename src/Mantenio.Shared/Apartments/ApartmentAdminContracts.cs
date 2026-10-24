using System.ComponentModel.DataAnnotations;

namespace Mantenio.Shared.Apartments;

public sealed record ApartmentAdminItem(
    Guid Id,
    string Code,
    string Tower,
    string Number,
    bool IsFrozen,
    decimal? SpecialFee,
    int ResidentCount);

public sealed record TowerItem(Guid Id, string Name, int SortOrder, int ApartmentCount);

public sealed record CreateTowerRequest([property: Required, MaxLength(50)] string Name);

public sealed record CreateApartmentRequest(Guid TowerId, [property: Required, MaxLength(20)] string Number);

public sealed record ImportApartmentsRequest([property: Required, MaxLength(200_000)] string Csv);

/// <summary>Dry run of a CSV import: what would be added and what is already there.</summary>
public sealed record ImportApartmentsPreview(
    IReadOnlyList<string> NewTowers,
    IReadOnlyList<string> NewApartments,
    int AlreadyExisting,
    string? Error);

public sealed record ImportApartmentsResult(int TowersAdded, int ApartmentsAdded);

public sealed record FreezeApartmentRequest([property: Required, MinLength(3), MaxLength(500)] string Reason);

public sealed record ApartmentFreezeItem(
    Guid Id,
    DateTimeOffset StartedAt,
    string Reason,
    string StartedByName,
    DateTimeOffset? EndedAt,
    string? EndReason,
    string? EndedByName);
