namespace Mantenio.Domain.Notifications;

public enum NotificationType
{
    /// <summary>A bank account was added, changed or removed. Must be acknowledged (popup at app start).</summary>
    BankAccountsChanged = 0,

    /// <summary>The global fee or the apartment's own fee changes from a period on.</summary>
    FeeChanged = 1,

    PaymentApproved = 2,
    PaymentRejected = 3,
    PaymentVoided = 4,

    /// <summary>Monthly reminder (day 15) to apartments with overdue debt. Neutral text, no consequences.</summary>
    DebtReminder = 5,
}

/// <summary>
/// An in-app notice for one user. The text is not stored: the UI renders it in Spanish from <see cref="Type"/>
/// and <see cref="Data"/> (JSON). Notices that need acknowledgement keep showing until the user confirms them.
/// </summary>
public sealed class UserNotification
{
    private UserNotification() { } // EF Core

    public UserNotification(Guid userId, NotificationType type, string data, bool requiresAcknowledgement, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        Id = Guid.CreateVersion7();
        UserId = userId;
        Type = type;
        Data = data;
        RequiresAcknowledgement = requiresAcknowledgement;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public NotificationType Type { get; private set; }
    public string Data { get; private set; } = "{}";
    public bool RequiresAcknowledgement { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }

    /// <summary>When the push copy went out (or was skipped: no devices, push off). Null = still to send.</summary>
    public DateTimeOffset? PushedAt { get; private set; }

    public bool IsPendingAcknowledgement => RequiresAcknowledgement && AcknowledgedAt is null;

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    public void MarkPushed(DateTimeOffset now) => PushedAt ??= now;

    /// <summary>Acknowledging also marks the notice as read.</summary>
    public void Acknowledge(DateTimeOffset now)
    {
        MarkRead(now);
        AcknowledgedAt ??= now;
    }
}
