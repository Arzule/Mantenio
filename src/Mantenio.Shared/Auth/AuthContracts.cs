using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Mantenio.Shared.Auth;

[JsonConverter(typeof(JsonStringEnumConverter<AccountRole>))]
public enum AccountRole
{
    Resident,
    Admin,
    Guard,
}

/// <summary>What the client must do after a login, registration or password step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SignInStep>))]
public enum SignInStep
{
    /// <summary>Tokens are included; the user is in.</summary>
    SignedIn,

    /// <summary>Admin login: a 6-digit code was emailed; send it with the challenge token.</summary>
    TwoFactorRequired,

    /// <summary>The email is not verified yet: a 6-digit code was emailed; send it with the challenge token.</summary>
    EmailConfirmationRequired,
}

public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record SignInResponse(SignInStep Step, AuthTokens? Tokens = null, string? ChallengeToken = null, string? MaskedEmail = null);

public sealed record LoginRequest(
    [property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required, MaxLength(128)] string Password,
    bool KeepSignedIn);

public sealed record VerifyCodeRequest(
    [property: Required, MaxLength(4096)] string ChallengeToken,
    [property: Required, RegularExpression(ValidationPatterns.SixDigits)] string Code);

public sealed record ResendCodeRequest([property: Required, MaxLength(4096)] string ChallengeToken);

public sealed record RefreshRequest([property: Required, MaxLength(512)] string RefreshToken);

public sealed record ForgotPasswordRequest([property: Required, EmailAddress, MaxLength(256)] string Email);

public sealed record ResetPasswordRequest(
    [property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required, RegularExpression(ValidationPatterns.SixDigits)] string Code,
    [property: Required, MinLength(PasswordRules.MinLength), MaxLength(128)] string NewPassword);

public sealed record ChangePasswordRequest(
    [property: Required, MaxLength(128)] string CurrentPassword,
    [property: Required, MinLength(PasswordRules.MinLength), MaxLength(128)] string NewPassword,
    bool KeepSignedIn);

public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    AccountRole Role,
    bool IsFounder,
    Guid? ApartmentId,
    string? ApartmentCode,
    bool MustChangePassword);

public static class PasswordRules
{
    public const int MinLength = 8;
}

public static class ValidationPatterns
{
    public const string SixDigits = "^[0-9]{6}$";
}
