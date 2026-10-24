using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Infrastructure.Notifications;

/// <summary>
/// Creates in-app notices (the caller saves them in its own transaction, next to the change and its audit event).
/// Push arrives in Phase 6; bank account changes are deliberately never sent by email.
/// </summary>
public sealed class NotificationService(AppDbContext db, TimeProvider time)
{
    public async Task<int> NotifyAllActiveUsersAsync(NotificationType type, object data, bool requiresAcknowledgement, CancellationToken cancellationToken)
    {
        var userIds = await db.Users.Where(u => u.IsActive).Select(u => u.Id).ToListAsync(cancellationToken);
        Add(userIds, type, data, requiresAcknowledgement);
        return userIds.Count;
    }

    public async Task<int> NotifyApartmentAsync(Guid apartmentId, NotificationType type, object data, CancellationToken cancellationToken)
    {
        var userIds = await db.Users.Where(u => u.IsActive && u.ApartmentId == apartmentId).Select(u => u.Id).ToListAsync(cancellationToken);
        Add(userIds, type, data, requiresAcknowledgement: false);
        return userIds.Count;
    }

    private void Add(IEnumerable<Guid> userIds, NotificationType type, object data, bool requiresAcknowledgement)
    {
        var json = AuditLog.Json(data);
        var now = time.GetUtcNow();
        db.Notifications.AddRange(userIds.Select(id => new UserNotification(id, type, json, requiresAcknowledgement, now)));
    }
}
