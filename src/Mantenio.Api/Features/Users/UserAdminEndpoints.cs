using System.Security.Claims;
using Mantenio.Api.Features.Invitations;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Identity;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Users;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Users;

internal static class UserAdminEndpoints
{
    public const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapUserAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").WithTags("Users").RequireAuthorization(MantenioPolicies.AdminOnly);

        group.MapGet("/", ListAsync).Produces<PagedResult<UserSummary>>();
        group.MapPost("/{id:guid}/deactivate", DeactivateAsync);
        group.MapPost("/{id:guid}/reactivate", ReactivateAsync);
        group.MapPut("/{id:guid}/apartment", ChangeApartmentAsync);

        return app;
    }

    /// <summary>Search by name, email or apartment code. Admins first, then by last name.</summary>
    private static async Task<IResult> ListAsync(
        AppDbContext db, string? search, bool? includeInactive, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);
        var skip = (Math.Max(page ?? 1, 1) - 1) * size;
        var query = db.Users.AsNoTracking();

        if (includeInactive != true)
        {
            query = query.Where(u => u.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.FirstName + " " + u.LastName, pattern, "\\")
                || EF.Functions.ILike(u.Email!, pattern, "\\")
                || (u.Apartment != null && EF.Functions.ILike(u.Apartment.Code, pattern, "\\")));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(u => u.Role == UserRole.Admin).ThenBy(u => u.LastName).ThenBy(u => u.FirstName)
            .Skip(skip)
            .Take(size)
            .Select(u => new UserSummary(
                u.Id,
                u.FirstName,
                u.LastName,
                u.Email!,
                u.Role == UserRole.Admin ? AccountRole.Admin : AccountRole.Resident,
                u.IsFounder,
                u.IsActive,
                u.ApartmentId,
                u.Apartment != null ? u.Apartment.Code : null,
                db.Users.Where(a => a.Id == u.AuthorizedById).Select(a => a.FirstName + " " + a.LastName).FirstOrDefault(),
                u.CreatedAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new PagedResult<UserSummary>(items, total));
    }

    /// <summary>Blocks the account and signs it out everywhere. The founder and yourself cannot be deactivated.</summary>
    private static Task<IResult> DeactivateAsync(
        Guid id, ChangeUserStatusRequest request, ClaimsPrincipal principal, AppDbContext db, SessionService sessions, TimeProvider time, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            var adminId = principal.GetUserId();
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
            {
                return ApiProblems.NotFound();
            }

            if (user.Id == adminId)
            {
                return ApiProblems.BadRequest(ErrorCodes.CannotModifySelf);
            }

            if (user.IsFounder)
            {
                return ApiProblems.Forbidden(ErrorCodes.FounderProtected);
            }

            if (user.IsActive)
            {
                var now = time.GetUtcNow();
                user.IsActive = false;
                user.DeactivatedAt = now;
                user.SecurityStamp = Guid.NewGuid().ToString(); // invalidates pending email codes
                await sessions.RevokeAllAsync(user.Id, ct);
                db.AuditEvents.Add(new AuditEvent(
                    AuditEntities.User, user.Id.ToString(), "Deactivated", AuditSource.Admin, adminId, now,
                    oldValues: AuditLog.Json(new { IsActive = true }),
                    newValues: AuditLog.Json(new { IsActive = false }),
                    reason: request.Reason.Trim(),
                    apartmentId: user.ApartmentId));
                await db.SaveChangesAsync(ct);
            }

            return (IResult)TypedResults.NoContent();
        }, cancellationToken);

    private static Task<IResult> ReactivateAsync(
        Guid id, ChangeUserStatusRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
            {
                return ApiProblems.NotFound();
            }

            if (!user.IsActive)
            {
                if (user.Role == UserRole.Admin && !await AdminSeats.HasSeatAsync(db, ct))
                {
                    return ApiProblems.Conflict(ErrorCodes.AdminLimitReached);
                }

                var now = time.GetUtcNow();
                user.IsActive = true;
                user.DeactivatedAt = null;
                db.AuditEvents.Add(new AuditEvent(
                    AuditEntities.User, user.Id.ToString(), "Reactivated", AuditSource.Admin, principal.GetUserId(), now,
                    oldValues: AuditLog.Json(new { IsActive = false }),
                    newValues: AuditLog.Json(new { IsActive = true }),
                    reason: request.Reason.Trim(),
                    apartmentId: user.ApartmentId));
                await db.SaveChangesAsync(ct);
            }

            return (IResult)TypedResults.NoContent();
        }, cancellationToken);

    /// <summary>Residents always keep an apartment; admins may have none.</summary>
    private static async Task<IResult> ChangeApartmentAsync(
        Guid id, ChangeUserApartmentRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return ApiProblems.NotFound();
        }

        if (request.ApartmentId is null && user.Role == UserRole.Resident)
        {
            return ApiProblems.BadRequest(ErrorCodes.ApartmentRequired);
        }

        if (request.ApartmentId is { } apartmentId && !await db.Apartments.AnyAsync(a => a.Id == apartmentId, cancellationToken))
        {
            return ApiProblems.BadRequest(ErrorCodes.ApartmentNotFound);
        }

        if (user.ApartmentId != request.ApartmentId)
        {
            var oldApartmentId = user.ApartmentId;
            user.ApartmentId = request.ApartmentId;
            db.AuditEvents.Add(new AuditEvent(
                AuditEntities.User, user.Id.ToString(), "ApartmentChanged", AuditSource.Admin, principal.GetUserId(), time.GetUtcNow(),
                oldValues: AuditLog.Json(new { ApartmentId = oldApartmentId }),
                newValues: AuditLog.Json(new { request.ApartmentId }),
                reason: request.Reason.Trim(),
                apartmentId: request.ApartmentId ?? oldApartmentId));
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
