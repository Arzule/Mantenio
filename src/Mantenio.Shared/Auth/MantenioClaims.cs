namespace Mantenio.Shared.Auth;

/// <summary>Claim names inside the access token. The API writes them and the clients read them.</summary>
public static class MantenioClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string FirstName = "given_name";
    public const string LastName = "family_name";
    public const string Role = "role";
    public const string ApartmentId = "apartment_id";
    public const string ApartmentCode = "apartment_code";

    /// <summary>The sign-in session (refresh-token family); the API rejects the token once that session is revoked.</summary>
    public const string SessionId = "sid";

    /// <summary>"true" for the founder admin.</summary>
    public const string Founder = "founder";

    /// <summary>"true" while the user must replace a temporary password; every other endpoint is denied.</summary>
    public const string MustChangePassword = "pwd_change";
}

/// <summary>Authorization policy names shared by the API and the UI.</summary>
public static class MantenioPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string FounderOnly = "FounderOnly";

    /// <summary>Admins and gatehouse staff (portería): payment review and invitation codes.</summary>
    public const string StaffOnly = "StaffOnly";

    /// <summary>Signed in, even with a pending password change (used by "me", change password and logout).</summary>
    public const string SignedIn = "SignedIn";
}
