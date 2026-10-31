namespace Mantenio.Domain.Identity;

public enum UserRole
{
    Resident = 0,
    Admin = 1,

    /// <summary>Gatehouse staff (portería): reviews and registers payments, never has an apartment.</summary>
    Guard = 2,
}
