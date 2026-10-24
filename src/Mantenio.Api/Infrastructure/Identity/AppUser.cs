using Mantenio.Domain.Condo;
using Mantenio.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace Mantenio.Api.Infrastructure.Identity;

/// <summary>An account (resident or admin). The email is also the user name.</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public UserRole Role { get; set; }

    /// <summary>Only one founder exists; only the founder can change the late-fee percentage.</summary>
    public bool IsFounder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Set for the seeded founder: the temporary password must be replaced before anything else.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Required for residents, optional for admins who also live in the condo.</summary>
    public Guid? ApartmentId { get; set; }

    public Apartment? Apartment { get; set; }

    /// <summary>Admin who generated the invitation code used to create this account (null for the founder).</summary>
    public Guid? AuthorizedById { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }

    public string FullName => $"{FirstName} {LastName}";

    public static AppUser Create(string email, string firstName, string lastName, UserRole role, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = email.Trim(),
        Email = email.Trim(),
        FirstName = firstName.Trim(),
        LastName = lastName.Trim(),
        Role = role,
        CreatedAt = now,
        LockoutEnabled = true,
    };
}
