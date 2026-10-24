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
}
