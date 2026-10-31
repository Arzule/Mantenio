using System.Security.Claims;
using Mantenio.Api.Features.Apartments;
using Mantenio.Api.Features.Auth;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.RateLimiting;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Identity;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Invitations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Invitations;

internal static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/invitations", CreateAsync)
            .WithTags("Invitations")
            .RequireAuthorization(MantenioPolicies.StaffOnly)
            .Produces<CreateInvitationResponse>();

        app.MapPost("/api/invitations/check", CheckAsync)
            .WithTags("Invitations")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Invitation)
            .Produces<CheckInvitationResponse>();

        app.MapPost("/api/auth/register", RegisterAsync)
            .WithTags("Auth")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Invitation)
            .Produces<SignInResponse>();

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateInvitationRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        InvitationCodeHasher hasher,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var role = Roles.ToUserRole(request.Role);

        // The gatehouse invites residents and other gatehouse staff; only admins create admins.
        if (role == UserRole.Admin && !principal.IsAdmin())
        {
            return ApiProblems.Forbidden(ErrorCodes.Forbidden);
        }

        if (role == UserRole.Guard && request.ApartmentId is not null)
        {
            return ApiProblems.BadRequest(ErrorCodes.GuardWithoutApartment);
        }

        if (!await AdminSeats.AnySeatLeftAsync(db, role, cancellationToken))
        {
            return ApiProblems.Conflict(SeatLimitCode(role));
        }

        var apartment = request.ApartmentId is { } apartmentId
            ? await ApartmentQueries.FindOptionAsync(db, apartmentId, cancellationToken)
            : null;
        if (request.ApartmentId is not null && apartment is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.ApartmentNotFound);
        }

        var now = time.GetUtcNow();
        var adminId = principal.GetUserId();

        // Two live codes must never share a value: draw again on the (unlikely) collision.
        string code;
        string hash;
        do
        {
            code = InvitationCode.GenerateCode();
            hash = hasher.Hash(code);
        }
        while (await db.InvitationCodes.AnyAsync(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now, cancellationToken));

        var invitation = new InvitationCode(hash, role, apartment?.Id, adminId, now);
        db.InvitationCodes.Add(invitation);
        db.AuditEvents.Add(new AuditEvent(
            AuditEntities.InvitationCode, invitation.Id.ToString(), "Created", AuditLog.SourceFor(principal), adminId, now,
            newValues: AuditLog.Json(new { Role = role.ToString(), invitation.ApartmentId, invitation.ExpiresAt }),
            apartmentId: invitation.ApartmentId));
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new CreateInvitationResponse(code, request.Role, apartment, invitation.ExpiresAt));
    }

    private static async Task<IResult> CheckAsync(
        CheckInvitationRequest request, AppDbContext db, InvitationCodeHasher hasher, TimeProvider time, CancellationToken cancellationToken)
    {
        var invitation = await FindRedeemableAsync(db, hasher.Hash(request.Code), time.GetUtcNow(), cancellationToken);
        if (invitation is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.InvitationInvalid);
        }

        var apartment = invitation.ApartmentId is { } apartmentId
            ? await ApartmentQueries.FindOptionAsync(db, apartmentId, cancellationToken)
            : null;
        return TypedResults.Ok(new CheckInvitationResponse(ToAccountRole(invitation.Role), apartment));
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AppDbContext db,
        UserManager<AppUser> users,
        InvitationCodeHasher hasher,
        SignInFlow flow,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var hash = hasher.Hash(request.Code);
        AppUser? created = null;
        IResult? problem;

        try
        {
            problem = await RedeemAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else redeemed the same code a moment earlier; the whole transaction was rolled back.
            return ApiProblems.BadRequest(ErrorCodes.InvitationInvalid);
        }

        if (problem is not null)
        {
            return problem;
        }

        // New accounts always confirm their email first; that code also signs them in.
        return await flow.ContinueAsync(created!, request.KeepSignedIn, cancellationToken);

        Task<IResult?> RedeemAsync() => db.InTransactionAsync<IResult?>(async ct =>
        {
            var now = time.GetUtcNow();
            var invitation = await FindRedeemableAsync(db, hash, now, ct);
            if (invitation is null)
            {
                return ApiProblems.BadRequest(ErrorCodes.InvitationInvalid);
            }

            if (!await AdminSeats.HasSeatAsync(db, invitation.Role, ct))
            {
                return ApiProblems.Conflict(SeatLimitCode(invitation.Role));
            }

            // A code bound to an apartment wins over whatever the form sent. Residents and admins live in the condo;
            // the gatehouse never has an apartment.
            var apartmentId = invitation.Role == UserRole.Guard ? null : invitation.ApartmentId ?? request.ApartmentId;
            if (apartmentId is null && invitation.Role != UserRole.Guard)
            {
                return ApiProblems.BadRequest(ErrorCodes.ApartmentRequired);
            }

            if (apartmentId is not null && !await db.Apartments.AnyAsync(a => a.Id == apartmentId, ct))
            {
                return ApiProblems.BadRequest(ErrorCodes.ApartmentNotFound);
            }

            var user = AppUser.Create(request.Email, request.FirstName, request.LastName, invitation.Role, now);
            user.ApartmentId = apartmentId;
            user.AuthorizedById = invitation.CreatedById;

            var result = await users.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                return result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName))
                    ? ApiProblems.Conflict(ErrorCodes.EmailTaken)
                    : result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal))
                        ? ApiProblems.BadRequest(ErrorCodes.WeakPassword)
                        : ApiProblems.BadRequest(ErrorCodes.ValidationFailed);
            }

            invitation.Redeem(user.Id, now);
            db.AuditEvents.Add(new AuditEvent(
                AuditEntities.User, user.Id.ToString(), "Registered",
                Roles.ToAuditSource(invitation.Role),
                user.Id, now,
                newValues: AuditLog.Json(new { user.Email, user.FirstName, user.LastName, Role = user.Role.ToString(), user.ApartmentId, user.AuthorizedById, InvitationCodeId = invitation.Id }),
                apartmentId: apartmentId));
            await db.SaveChangesAsync(ct);
            created = user;
            return null;
        }, cancellationToken);
    }

    private static Task<InvitationCode?> FindRedeemableAsync(AppDbContext db, string hash, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.InvitationCodes
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .FirstOrDefaultAsync(cancellationToken);

    private static AccountRole ToAccountRole(UserRole role) => Roles.ToAccountRole(role);

    private static string SeatLimitCode(UserRole role) => role == UserRole.Guard ? ErrorCodes.GuardLimitReached : ErrorCodes.AdminLimitReached;
}
