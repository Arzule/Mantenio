using System.ComponentModel.DataAnnotations;
using Mantenio.Shared.Auth;

namespace Mantenio.Shared.Users;

public sealed record UserSummary(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    AccountRole Role,
    bool IsFounder,
    bool IsActive,
    Guid? ApartmentId,
    string? ApartmentCode,
    string? AuthorizedByName,
    DateTimeOffset CreatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

public sealed record ChangeUserStatusRequest([property: Required, MinLength(3), MaxLength(500)] string Reason);

/// <summary>Residents and admins always need an apartment; the gatehouse never has one.</summary>
public sealed record ChangeUserApartmentRequest(
    Guid? ApartmentId,
    [property: Required, MinLength(3), MaxLength(500)] string Reason);
