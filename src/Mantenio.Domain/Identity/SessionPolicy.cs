namespace Mantenio.Domain.Identity;

/// <summary>How long sessions last and which accounts need a second factor.</summary>
public static class SessionPolicy
{
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Short-lived token that links the password step with the email-code step.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(15);

    public static TimeSpan RefreshTokenLifetime(UserRole role, bool keepSignedIn) => (role, keepSignedIn) switch
    {
        (UserRole.Admin, true) => TimeSpan.FromDays(30),
        (UserRole.Resident, true) => TimeSpan.FromDays(90),
        _ => TimeSpan.FromHours(12),
    };

    /// <summary>Admins always confirm a code sent by email after the password.</summary>
    public static bool RequiresTwoFactor(UserRole role) => role == UserRole.Admin;
}
