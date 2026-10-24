namespace Mantenio.Domain.Condo;

/// <summary>One period during which an apartment was frozen, with who started and ended it and why.</summary>
public sealed class ApartmentFreeze
{
    private ApartmentFreeze() { } // EF Core

    internal ApartmentFreeze(Guid apartmentId, string reason, Guid startedById, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Id = Guid.CreateVersion7();
        ApartmentId = apartmentId;
        Reason = reason.Trim();
        StartedById = startedById;
        StartedAt = startedAt;
    }

    public Guid Id { get; private set; }
    public Guid ApartmentId { get; private set; }
    public string Reason { get; private set; } = "";
    public Guid StartedById { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public Guid? EndedById { get; private set; }
    public string? EndReason { get; private set; }

    public bool IsActive => EndedAt is null;

    internal void End(string reason, Guid endedById, DateTimeOffset endedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!IsActive)
        {
            throw new InvalidOperationException("The freeze already ended.");
        }

        EndReason = reason.Trim();
        EndedById = endedById;
        EndedAt = endedAt;
    }
}
