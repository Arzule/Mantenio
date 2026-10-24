namespace Mantenio.Api.Infrastructure.Identity;

/// <summary>
/// One refresh token of a session. Every refresh rotates it: the old one is revoked and points to its
/// replacement. All tokens of one sign-in share a <see cref="FamilyId"/>; reusing a rotated token revokes the family.
/// Only the SHA-256 hash of the token is stored.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid FamilyId { get; set; }
    public bool KeepSignedIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}
