using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Shared.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Mantenio.Api.Infrastructure.Auth;

internal static class AuthSetup
{
    public static IServiceCollection AddMantenioAuth(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .Validate(o => o.SigningKey.Length >= 32, "Jwt:SigningKey must have at least 32 characters.")
            .ValidateOnStart();

        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // Residents of all ages use this: length and a digit, no symbol rules.
                options.Password.RequiredLength = PasswordRules.MinLength;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;

                // Wrong passwords and wrong email codes both count.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                // Every emailed code (2FA, email confirmation, password reset) is a 6-digit TOTP; no links.
                options.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultEmailProvider;
                options.Tokens.PasswordResetTokenProvider = TokenOptions.DefaultEmailProvider;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddTokenProvider<EmailTokenProvider<AppUser>>(TokenOptions.DefaultEmailProvider);

        services.AddSingleton<TokenService>();
        services.AddSingleton<InvitationCodeHasher>();
        services.AddScoped<SessionService>();
        services.AddMemoryCache();
        services.AddSingleton<SessionGuard>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService>((options, tokens) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokens.ValidationParameters(JwtOptions.AccessAudience);
                options.Events = new JwtBearerEvents { OnTokenValidated = RejectRevokedSessionAsync };
            });

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(Policy(_ => { }))
            // Deny by default: endpoints must opt out with AllowAnonymous (see AuthorizationCoverageTests).
            .SetFallbackPolicy(Policy(_ => { }))
            .AddPolicy(MantenioPolicies.SignedIn, p => p.RequireAuthenticatedUser())
            .AddPolicy(MantenioPolicies.AdminOnly, Policy(p => p.RequireClaim(MantenioClaims.Role, nameof(AccountRole.Admin))))
            .AddPolicy(MantenioPolicies.StaffOnly, Policy(p => p.RequireClaim(MantenioClaims.Role, nameof(AccountRole.Admin), nameof(AccountRole.Guard))))
            .AddPolicy(MantenioPolicies.FounderOnly, Policy(p => p
                .RequireClaim(MantenioClaims.Role, nameof(AccountRole.Admin))
                .RequireClaim(MantenioClaims.Founder, "true")));

        return services;
    }

    /// <summary>A password change or logout elsewhere ends this session now, not when the access token expires.</summary>
    private static async Task RejectRevokedSessionAsync(TokenValidatedContext context)
    {
        var guard = context.HttpContext.RequestServices.GetRequiredService<SessionGuard>();
        if (!Guid.TryParse(context.Principal?.FindFirst(MantenioClaims.SessionId)?.Value, out var sessionId)
            || !await guard.IsActiveAsync(sessionId, context.HttpContext.RequestAborted))
        {
            context.Fail("The session was revoked.");
        }
    }

    /// <summary>Signed in and without a pending temporary-password change.</summary>
    private static AuthorizationPolicy Policy(Action<AuthorizationPolicyBuilder> configure)
    {
        var builder = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => !context.User.HasClaim(MantenioClaims.MustChangePassword, "true"));
        configure(builder);
        return builder.Build();
    }
}
