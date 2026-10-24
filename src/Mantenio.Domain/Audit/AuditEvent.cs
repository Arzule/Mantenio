namespace Mantenio.Domain.Audit;

/// <summary>
/// Append-only history entry. Every ledger or configuration change writes one in the same transaction.
/// Old and new values are JSON snapshots of the fields that changed.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent() { } // EF Core

    public AuditEvent(
        string entityType,
        string entityId,
        string action,
        AuditSource source,
        Guid? actorUserId,
        DateTimeOffset createdAt,
        string? oldValues = null,
        string? newValues = null,
        string? reason = null,
        Guid? apartmentId = null,
        Guid? chargeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (source == AuditSource.System && actorUserId is not null)
        {
            throw new ArgumentException("System events have no actor.", nameof(actorUserId));
        }

        Id = Guid.CreateVersion7();
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        Source = source;
        ActorUserId = actorUserId;
        CreatedAt = createdAt;
        OldValues = oldValues;
        NewValues = newValues;
        Reason = reason;
        ApartmentId = apartmentId;
        ChargeId = chargeId;
    }

    public Guid Id { get; private set; }
    public string EntityType { get; private set; } = "";
    public string EntityId { get; private set; } = "";
    public string Action { get; private set; } = "";
    public AuditSource Source { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? OldValues { get; private set; }
    public string? NewValues { get; private set; }
    public string? Reason { get; private set; }
    public Guid? ApartmentId { get; private set; }
    public Guid? ChargeId { get; private set; }
}
