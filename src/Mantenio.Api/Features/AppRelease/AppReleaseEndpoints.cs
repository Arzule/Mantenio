using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Shared.SystemInfo;

namespace Mantenio.Api.Features.AppRelease;

/// <summary>
/// Public: the latest Android version (the app compares it with its own) and a stable download link that redirects to
/// the APK in the files bucket. The web's /app address points here, so the brochure link never changes.
/// </summary>
internal static class AppReleaseEndpoints
{
    public const string DownloadPath = "/api/app/android";

    public static IEndpointRouteBuilder MapAppReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/app").WithTags("App").AllowAnonymous();

        group.MapGet("/android/version", async (AppReleaseService releases, CancellationToken cancellationToken) =>
                await releases.GetAsync(cancellationToken) is { } release
                    ? Results.Ok(new AndroidRelease(release.VersionCode, release.VersionName, release.MinVersionCode, DownloadPath))
                    : ApiProblems.NotFound())
            .Produces<AndroidRelease>();

        group.MapGet("/android", async (AppReleaseService releases, IFileStorage storage, CancellationToken cancellationToken) =>
            await releases.GetAsync(cancellationToken) is { } release
                ? Results.Redirect(storage.GetReadUrl(release.ApkKey, TimeSpan.FromHours(1)).AbsoluteUri)
                : ApiProblems.NotFound());

        return app;
    }
}
