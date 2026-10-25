using Mantenio.Shared.Condos;
using Mantenio.Shared.SystemInfo;

namespace Mantenio.Api.Features.SystemInfo;

internal static class SystemInfoEndpoints
{
    public static IEndpointRouteBuilder MapSystemInfoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system").WithTags("System");

        // Public, non-sensitive information used by clients to confirm they talk to the right condo instance.
        group.MapGet("/info", () =>
        {
            var profile = CondoProfile.Current;
            return TypedResults.Ok(new SystemInfoResponse(
                profile.Slug,
                profile.DisplayName,
                typeof(SystemInfoEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        })
        .WithName("GetSystemInfo")
        .AllowAnonymous();

        return app;
    }
}
