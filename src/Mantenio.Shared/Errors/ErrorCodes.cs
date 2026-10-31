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
    public const string GuardLimitReached = "guard_limit_reached";
    public const string GuardWithoutApartment = "guard_without_apartment";
    public const string ApartmentRequired = "apartment_required";
    public const string ApartmentNotFound = "apartment_not_found";
    public const string FounderProtected = "founder_protected";
    public const string CannotModifySelf = "cannot_modify_self";

    // Condo configuration
    public const string InvalidPeriod = "invalid_period";
    public const string InvalidBankAccount = "invalid_bank_account";
    public const string ApartmentAlreadyFrozen = "apartment_already_frozen";
    public const string ApartmentNotFrozen = "apartment_not_frozen";
    public const string ApartmentInUse = "apartment_in_use";
    public const string TowerExists = "tower_exists";
    public const string TowerNotFound = "tower_not_found";
    public const string ApartmentExists = "apartment_exists";
    public const string InvalidCsv = "invalid_csv";

    // Ledger
    public const string DuplicateOperationNumber = "duplicate_operation_number";
    public const string OwnApartmentReview = "own_apartment_review";
    public const string OwnSubmissionReview = "own_submission_review";
    public const string AdminApartmentRequired = "admin_apartment_required";
    public const string InvalidImage = "invalid_image";
    public const string ProofNotPending = "proof_not_pending";
    public const string ProofNotApproved = "proof_not_approved";
}
