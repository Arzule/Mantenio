using System.ComponentModel.DataAnnotations;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;

namespace Mantenio.Shared.Invitations;

public sealed record CreateInvitationRequest(AccountRole Role, Guid? ApartmentId);

public sealed record CreateInvitationResponse(string Code, AccountRole Role, ApartmentOption? Apartment, DateTimeOffset ExpiresAt);

public sealed record CheckInvitationRequest([property: Required, RegularExpression(ValidationPatterns.SixDigits)] string Code);

/// <summary>What the registration form needs to know about a valid code.</summary>
public sealed record CheckInvitationResponse(AccountRole Role, ApartmentOption? Apartment);

public sealed record RegisterRequest(
    [property: Required, RegularExpression(ValidationPatterns.SixDigits)] string Code,
    [property: Required, MaxLength(100)] string FirstName,
    [property: Required, MaxLength(100)] string LastName,
    [property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required, MinLength(PasswordRules.MinLength), MaxLength(128)] string Password,
    Guid? ApartmentId,
    bool KeepSignedIn);
