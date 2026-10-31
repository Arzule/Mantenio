using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Audit;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Mantenio.Api.Features.Account;

internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Reachable with a pending temporary-password change, so the user can fix it.
        var group = app.MapGroup("/api/account").WithTags("Account").RequireAuthorization(MantenioPolicies.SignedIn);

        group.MapGet("/me", GetCurrentUserAsync).Produces<CurrentUserResponse>();
        group.MapPost("/password", ChangePasswordAsync).Produces<AuthTokens>();

        return app;
    }

    private static async Task<IResult> GetCurrentUserAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var user = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new CurrentUserResponse(
                u.Id,
                u.Email!,
                u.FirstName,
                u.LastName,
                Roles.ToAccountRole(u.Role),
                u.IsFounder,
                u.ApartmentId,
                u.Apartment != null ? u.Apartment.Code : null,
                u.MustChangePassword))
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? ApiProblems.NotFound() : TypedResults.Ok(user);
    }

    /// <summary>Changes the password, signs out every other session and returns a fresh one for this device.</summary>
    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        SessionService sessions,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(principal.GetUserId().ToString());
        if (user is not { IsActive: true })
        {
            return ApiProblems.Unauthorized(ErrorCodes.Unauthorized);
        }

        if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
        {
            await users.AccessFailedAsync(user);
            return ApiProblems.BadRequest(ErrorCodes.WrongPassword);
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return ApiProblems.BadRequest(ErrorCodes.WeakPassword);
        }

        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await sessions.RevokeAllAsync(user.Id, cancellationToken);

        db.AuditEvents.Add(new AuditEvent(
            AuditEntities.User, user.Id.ToString(), "PasswordChanged", AuditLog.SourceFor(principal), user.Id, time.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(await sessions.StartAsync(user, request.KeepSignedIn, cancellationToken));
    }
}
