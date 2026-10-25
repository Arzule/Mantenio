namespace Mantenio.Domain.Notifications;

/// <summary>A phone that receives push notifications for a user (Firebase Cloud Messaging registration token).</summary>
public sealed class UserDevice
{
    private UserDevice() { } // EF Core

    public UserDevice(Guid userId, string token, string platform, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        Id = Guid.CreateVersion7();
        UserId = userId;
        Token = token;
        Platform = platform;
        CreatedAt = now;
        LastSeenAt = now;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Token { get; private set; } = "";
    public string Platform { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>The same phone signed in again (maybe as another user of the family).</summary>
    public void Refresh(Guid userId, DateTimeOffset now)
    {
        UserId = userId;
        LastSeenAt = now;
    }
}
