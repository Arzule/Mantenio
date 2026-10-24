using System.Security.Claims;
using System.Text.Json;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Notifications;
using Mantenio.Shared.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Notifications;

/// <summary>The caller's own notices. A notice of another user answers 404.</summary>
internal static class NotificationEndpoints
{
    public const int PageSize = 50;

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();

        group.MapGet("/", ListAsync).Produces<NotificationPage>();
        group.MapGet("/pending", ListPendingAsync).Produces<IReadOnlyList<NotificationItem>>();
        group.MapPost("/{id:guid}/read", MarkReadAsync);
        group.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync);
        group.MapPost("/read-all", MarkAllReadAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var rows = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(PageSize)
            .ToListAsync(cancellationToken);
        var unread = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);
        return TypedResults.Ok(new NotificationPage(rows.Select(ToItem).ToList(), unread));
    }

    /// <summary>Notices the app must show as a blocking popup until the user taps "Entendido".</summary>
    private static async Task<IResult> ListPendingAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var rows = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId && n.RequiresAcknowledgement && n.AcknowledgedAt == null)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(rows.Select(ToItem).ToList());
    }

    private static Task<IResult> MarkReadAsync(Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        UpdateAsync(id, principal, db, n => n.MarkRead(time.GetUtcNow()), cancellationToken);

    private static Task<IResult> AcknowledgeAsync(Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        UpdateAsync(id, principal, db, n => n.Acknowledge(time.GetUtcNow()), cancellationToken);

    /// <summary>Marks everything as read; notices that need an explicit acknowledgement stay pending.</summary>
    private static async Task<IResult> MarkAllReadAsync(ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var now = time.GetUtcNow();
        await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, ClaimsPrincipal principal, AppDbContext db, Action<UserNotification> change, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.UserId == userId, cancellationToken);
        if (notification is null)
        {
            return ApiProblems.NotFound();
        }

        change(notification);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static NotificationItem ToItem(UserNotification n)
    {
        using var data = JsonDocument.Parse(n.Data);
        return new NotificationItem(
            n.Id,
            n.Type == NotificationType.BankAccountsChanged ? NotificationKind.BankAccountsChanged : NotificationKind.FeeChanged,
            data.RootElement.Clone(),
            n.RequiresAcknowledgement,
            n.CreatedAt,
            n.ReadAt,
            n.AcknowledgedAt);
    }
}
