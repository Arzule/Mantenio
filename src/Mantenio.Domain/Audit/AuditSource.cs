namespace Mantenio.Domain.Audit;

public enum AuditSource
{
    Resident = 0,
    Admin = 1,

    /// <summary>Gatehouse staff (portería).</summary>
    Guard = 3,

    /// <summary>Automatic actions (jobs, seeding). The actor is always null.</summary>
    System = 2,
}
