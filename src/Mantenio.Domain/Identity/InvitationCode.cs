using System.Globalization;
using System.Security.Cryptography;

namespace Mantenio.Domain.Identity;

/// <summary>
/// Single-use 6-digit code an admin hands out so someone can create an account.
/// Only a keyed hash of the code is stored; the plain code is shown once to the admin.
/// </summary>
public sealed class InvitationCode
{
    public const int Length = 6;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private InvitationCode() { } // EF Core

    public InvitationCode(string codeHash, UserRole role, Guid? apartmentId, Guid createdById, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        Id = Guid.CreateVersion7();
        CodeHash = codeHash;
        Role = role;
        ApartmentId = apartmentId;
        CreatedById = createdById;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public Guid Id { get; private set; }
    public string CodeHash { get; private set; } = "";
    public UserRole Role { get; private set; }

    /// <summary>When set, the new account is bound to this apartment and the person cannot pick another one.</summary>
    public Guid? ApartmentId { get; private set; }

    public Guid CreatedById { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid? UsedById { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    public bool IsRedeemable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;

    public void Redeem(Guid userId, DateTimeOffset now)
    {
        if (!IsRedeemable(now))
        {
            throw new InvalidOperationException("The invitation code is expired or already used.");
        }

        UsedById = userId;
        UsedAt = now;
    }

    /// <summary>A uniformly random code from 000000 to 999999.</summary>
    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static bool IsWellFormed(string? code) =>
        code is { Length: Length } && code.All(char.IsAsciiDigit);
}
