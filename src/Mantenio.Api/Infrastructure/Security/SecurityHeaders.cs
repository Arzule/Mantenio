using Mantenio.Api.Infrastructure.RateLimiting;
using Mantenio.Api.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;

namespace Mantenio.Api.Infrastructure.Security;

internal static class SecurityHeaders
{
    /// <summary>
    /// The API only returns JSON and files: nothing may embed, frame or run it, and browsers must not guess types.
    /// HSTS outside development (Container Apps terminates TLS; the forwarded headers carry the scheme).
    /// </summary>
    public static IApplicationBuilder UseMantenioSecurityHeaders(this WebApplication app)
    {
        var hsts = !app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing");
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (!context.Request.Path.StartsWithSegments("/api/files"))
            {
                // Local file links (development) are opened in the browser's PDF viewer, which a strict CSP blocks.
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }
            if (hsts)
            {
                headers.StrictTransportSecurity = "max-age=31536000";
            }

            await next(context);
        });
    }

    /// <summary>Image uploads: at most ~12 MB per request and the per-user upload rate limit.</summary>
    public static RouteHandlerBuilder WithUploadLimits(this RouteHandlerBuilder builder) =>
        builder
            .WithMetadata(new RequestSizeLimitAttribute(ProofImage.MaxUploadBytes + (2 * 1024 * 1024)))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = ProofImage.MaxUploadBytes + (1024 * 1024) })
            .RequireRateLimiting(RateLimitPolicies.Upload);
}
