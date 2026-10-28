using System.Threading.RateLimiting;
using Mantenio.Shared.Errors;

namespace Mantenio.Api.Infrastructure.RateLimiting;

/// <summary>Per-IP limits for the endpoints an attacker would hammer. Limits are configurable ("RateLimiting").</summary>
public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimiting";

    /// <summary>Login, 2FA and email-code verification.</summary>
    public int AuthPerMinute { get; set; } = 10;

    /// <summary>Anything that sends an email (forgot password, resend code).</summary>
    public int EmailPer15Minutes { get; set; } = 5;

    /// <summary>Checking and redeeming invitation codes (6 digits, so this is the brute-force guard).</summary>
    public int InvitationPerMinute { get; set; } = 5;

    public int RefreshPerMinute { get; set; } = 30;

    /// <summary>Payment-proof uploads per signed-in user (images are the heaviest requests).</summary>
    public int UploadsPerHour { get; set; } = 30;

    /// <summary>Admins upload more (vouchers brought in person, historical payments with their photos).</summary>
    public int AdminUploadsPerHour { get; set; } = 300;
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Email = "email";
    public const string Invitation = "invitation";
    public const string Refresh = "refresh";
    public const string Upload = "upload";
}

internal static class RateLimitingSetup
{
    public static IServiceCollection AddMantenioRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(RateLimitSettings.SectionName).Get<RateLimitSettings>() ?? new();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Extensions = { ["code"] = ErrorCodes.TooManyRequests },
                    },
                });
            };

            options.AddPolicy(RateLimitPolicies.Auth, http => PerClient(http, settings.AuthPerMinute, TimeSpan.FromMinutes(1)));
            options.AddPolicy(RateLimitPolicies.Email, http => PerClient(http, settings.EmailPer15Minutes, TimeSpan.FromMinutes(15)));
            options.AddPolicy(RateLimitPolicies.Invitation, http => PerClient(http, settings.InvitationPerMinute, TimeSpan.FromMinutes(1)));
            options.AddPolicy(RateLimitPolicies.Refresh, http => PerClient(http, settings.RefreshPerMinute, TimeSpan.FromMinutes(1)));
            options.AddPolicy(RateLimitPolicies.Upload, http =>
            {
                var isAdmin = http.User.HasClaim(Mantenio.Shared.Auth.MantenioClaims.Role, nameof(Mantenio.Shared.Auth.AccountRole.Admin));
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.User.FindFirst(Mantenio.Shared.Auth.MantenioClaims.UserId)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = isAdmin ? settings.AdminUploadsPerHour : settings.UploadsPerHour,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }

    private static RateLimitPartition<string> PerClient(HttpContext http, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window, QueueLimit = 0 });
}
