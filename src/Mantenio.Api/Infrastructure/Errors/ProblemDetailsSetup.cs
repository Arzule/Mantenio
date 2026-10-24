using Mantenio.Shared.Errors;

namespace Mantenio.Api.Infrastructure.Errors;

internal static class ProblemDetailsSetup
{
    /// <summary>
    /// Every error response is RFC 9457 ProblemDetails with a machine-readable <c>code</c>
    /// (see <see cref="ErrorCodes"/>) that the UI translates to Spanish.
    /// </summary>
    public static IServiceCollection AddMantenioProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
                {
                    StatusCodes.Status400BadRequest => ErrorCodes.ValidationFailed,
                    StatusCodes.Status401Unauthorized => ErrorCodes.Unauthorized,
                    StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
                    StatusCodes.Status404NotFound => ErrorCodes.NotFound,
                    StatusCodes.Status409Conflict => ErrorCodes.Conflict,
                    StatusCodes.Status429TooManyRequests => ErrorCodes.TooManyRequests,
                    StatusCodes.Status503ServiceUnavailable => ErrorCodes.DatabaseUnavailable,
                    _ => ErrorCodes.InternalError,
                });
            };
        });
}
