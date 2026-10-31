using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Domain.Identity;
using Mantenio.Shared.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mantenio.Api.Infrastructure.Auth;

public enum ChallengePurpose
{
    TwoFactor,
    ConfirmEmail,
}

public sealed record Challenge(Guid UserId, ChallengePurpose Purpose, bool KeepSignedIn);

/// <summary>Creates access tokens and the short-lived challenge tokens that link the password and email-code steps.</summary>
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    private const string PurposeClaim = "purpose";
    private const string KeepSignedInClaim = "keep";

    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public SymmetricSecurityKey SigningKey { get; } = new(Encoding.UTF8.GetBytes(options.Value.SigningKey));

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(AppUser user, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = time.GetUtcNow();
        var expiresAt = now + SessionPolicy.AccessTokenLifetime;

        var claims = new Dictionary<string, object>
        {
            [MantenioClaims.UserId] = user.Id.ToString(),
            [MantenioClaims.Email] = user.Email ?? "",
            [MantenioClaims.Name] = user.FullName,
            [MantenioClaims.FirstName] = user.FirstName,
            [MantenioClaims.LastName] = user.LastName,
            [MantenioClaims.Role] = user.Role.ToString(),
            [MantenioClaims.SessionId] = sessionId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };
        if (user.IsFounder) claims[MantenioClaims.Founder] = "true";
        if (user.MustChangePassword) claims[MantenioClaims.MustChangePassword] = "true";
        if (user.ApartmentId is { } apartmentId) claims[MantenioClaims.ApartmentId] = apartmentId.ToString();
        if (user.Apartment is { } apartment) claims[MantenioClaims.ApartmentCode] = apartment.Code;

        return (Create(claims, JwtOptions.AccessAudience, now, expiresAt), expiresAt);
    }

    public string CreateChallenge(AppUser user, ChallengePurpose purpose, bool keepSignedIn)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = time.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            [MantenioClaims.UserId] = user.Id.ToString(),
            [PurposeClaim] = purpose.ToString(),
            [KeepSignedInClaim] = keepSignedIn ? "true" : "false",
        };
        return Create(claims, JwtOptions.ChallengeAudience, now, now + SessionPolicy.ChallengeLifetime);
    }

    /// <summary>Returns null when the token is invalid, expired or meant for another purpose.</summary>
    public async Task<Challenge?> ReadChallengeAsync(string token, ChallengePurpose purpose)
    {
        var result = await _handler.ValidateTokenAsync(token, ValidationParameters(JwtOptions.ChallengeAudience));
        if (!result.IsValid
            || !result.Claims.TryGetValue(PurposeClaim, out var tokenPurpose)
            || !string.Equals(tokenPurpose as string, purpose.ToString(), StringComparison.Ordinal)
            || !Guid.TryParse(result.Claims[MantenioClaims.UserId] as string, out var userId))
        {
            return null;
        }

        var keepSignedIn = string.Equals(result.Claims[KeepSignedInClaim] as string, "true", StringComparison.Ordinal);
        return new Challenge(userId, purpose, keepSignedIn);
    }

    public TokenValidationParameters ValidationParameters(string audience) => new()
    {
        ValidIssuer = _options.Issuer,
        ValidAudience = audience,
        IssuerSigningKey = SigningKey,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = MantenioClaims.Name,
        RoleClaimType = MantenioClaims.Role,
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    /// <summary>A new opaque refresh token (256 random bits, base64url).</summary>
    public static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private string Create(Dictionary<string, object> claims, string audience, DateTimeOffset now, DateTimeOffset expiresAt) =>
        _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(MantenioClaims.UserId)
            ?? throw new InvalidOperationException("The principal has no user id."), CultureInfo.InvariantCulture);

    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        principal.HasClaim(MantenioClaims.Role, nameof(UserRole.Admin));

    public static bool IsGuard(this ClaimsPrincipal principal) =>
        principal.HasClaim(MantenioClaims.Role, nameof(UserRole.Guard));

    public static bool IsStaff(this ClaimsPrincipal principal) => principal.IsAdmin() || principal.IsGuard();
}
