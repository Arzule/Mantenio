namespace Mantenio.Domain.Identity;

/// <summary>How long sessions last and which accounts need a second factor.</summary>
public static class SessionPolicy
{
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Short-lived token that links the password step with the email-code step.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(15);

    public static TimeSpan RefreshTokenLifetime(UserRole role, bool keepSignedIn) => (role, keepSignedIn) switch
    {
        (UserRole.Admin or UserRole.Guard, true) => TimeSpan.FromDays(30),
        (UserRole.Resident, true) => TimeSpan.FromDays(90),
        _ => TimeSpan.FromHours(12),
    };

    /// <summary>Staff (admins and gatehouse) always confirm a code sent by email after the password.</summary>
    public static bool RequiresTwoFactor(UserRole role) => role is UserRole.Admin or UserRole.Guard;

    /// <summary>Admins and gatehouse staff review and register payments.</summary>
    public static bool IsStaff(UserRole role) => role is UserRole.Admin or UserRole.Guard;
}
