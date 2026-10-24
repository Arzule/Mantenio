namespace Mantenio.Shared.Errors;

/// <summary>
/// Machine-readable error codes returned by the API (ProblemDetails "code" extension).
/// The UI maps each code to a Spanish message; never show these strings to users.
/// </summary>
public static class ErrorCodes
{
    public const string InternalError = "internal_error";
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string Conflict = "concurrency_conflict";
    public const string DatabaseUnavailable = "database_unavailable";
    public const string TooManyRequests = "too_many_requests";
    public const string EmailUnavailable = "email_unavailable";

    // Identity and access
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string AccountInactive = "account_inactive";
    public const string InvalidCode = "invalid_code";
    public const string ChallengeExpired = "challenge_expired";
    public const string InvalidRefreshToken = "invalid_refresh_token";
    public const string PasswordChangeRequired = "password_change_required";
    public const string WeakPassword = "weak_password";
    public const string WrongPassword = "wrong_password";
    public const string EmailTaken = "email_taken";
    public const string InvitationInvalid = "invitation_invalid";
    public const string AdminLimitReached = "admin_limit_reached";
    public const string ApartmentRequired = "apartment_required";
    public const string ApartmentNotFound = "apartment_not_found";
    public const string FounderProtected = "founder_protected";
    public const string CannotModifySelf = "cannot_modify_self";
}
