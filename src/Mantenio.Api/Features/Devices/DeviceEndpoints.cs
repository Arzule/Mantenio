using System.Security.Claims;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Push;
using Mantenio.Domain.Notifications;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Devices;

/// <summary>Phones register their Firebase token after sign-in and remove it on sign-out.</summary>
internal static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").WithTags("Devices").RequireAuthorization();
        group.MapPost("/", RegisterAsync);
        group.MapPost("/unregister", UnregisterAsync);
        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterDeviceRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, FounderDevices founderDevices,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var now = time.GetUtcNow();
        var device = await db.UserDevices.SingleOrDefaultAsync(d => d.Token == request.Token, cancellationToken);
        if (device is null)
        {
            db.UserDevices.Add(new UserDevice(userId, request.Token, request.Platform, now));
        }
        else
        {
            device.Refresh(userId, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await SyncFounderAsync(principal, founderDevices, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UnregisterAsync(
        UnregisterDeviceRequest request, ClaimsPrincipal principal, AppDbContext db, FounderDevices founderDevices,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        await db.UserDevices.Where(d => d.Token == request.Token && d.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await SyncFounderAsync(principal, founderDevices, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The founder's phones also get the database-down alert, which cannot read them from the database.</summary>
    private static Task SyncFounderAsync(ClaimsPrincipal principal, FounderDevices founderDevices, CancellationToken cancellationToken) =>
        principal.HasClaim(MantenioClaims.Founder, "true") ? founderDevices.SyncAsync(cancellationToken) : Task.CompletedTask;
}
