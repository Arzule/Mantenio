namespace Mantenio.Api.Infrastructure.Auth;

/// <summary>Bound from the "Jwt" section. The signing key is a secret (Aspire parameter locally, Container Apps secret in prod).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string AccessAudience = "mantenio-api";
    public const string ChallengeAudience = "mantenio-challenge";

    public string Issuer { get; set; } = "mantenio";

    /// <summary>At least 32 characters. Also derives the key that hashes invitation codes.</summary>
    public string SigningKey { get; set; } = "";
}
