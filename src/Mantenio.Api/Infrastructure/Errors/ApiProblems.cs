using Mantenio.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Mantenio.Api.Infrastructure.Errors;

/// <summary>ProblemDetails responses carrying an explicit <see cref="ErrorCodes"/> value.</summary>
internal static class ApiProblems
{
    public static ProblemHttpResult Error(int statusCode, string code) =>
        TypedResults.Problem(statusCode: statusCode, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static ProblemHttpResult BadRequest(string code) => Error(StatusCodes.Status400BadRequest, code);

    public static ProblemHttpResult Unauthorized(string code) => Error(StatusCodes.Status401Unauthorized, code);

    public static ProblemHttpResult Forbidden(string code) => Error(StatusCodes.Status403Forbidden, code);

    public static ProblemHttpResult NotFound(string code = ErrorCodes.NotFound) => Error(StatusCodes.Status404NotFound, code);

    public static ProblemHttpResult Conflict(string code) => Error(StatusCodes.Status409Conflict, code);
}
