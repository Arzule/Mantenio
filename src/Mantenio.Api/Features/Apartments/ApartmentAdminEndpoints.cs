using System.Security.Claims;
using Mantenio.Api.Features.Settings;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Condo;
using Mantenio.Domain.Fees;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Apartments;

internal static class ApartmentAdminEndpoints
{
    public static IEndpointRouteBuilder MapApartmentAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Apartments").RequireAuthorization(MantenioPolicies.AdminOnly);

        group.MapGet("/towers", ListTowersAsync).Produces<IReadOnlyList<TowerItem>>();
        group.MapPost("/towers", CreateTowerAsync).Produces<TowerItem>();
        group.MapGet("/apartments", ListApartmentsAsync).Produces<IReadOnlyList<ApartmentAdminItem>>();
        group.MapPost("/apartments", CreateApartmentAsync).Produces<ApartmentAdminItem>();
        group.MapDelete("/apartments/{id:guid}", DeleteApartmentAsync);
        group.MapPost("/apartments/import/preview", PreviewImportAsync).Produces<ImportApartmentsPreview>();
        group.MapPost("/apartments/import", ImportAsync).Produces<ImportApartmentsResult>();
        group.MapPost("/apartments/{id:guid}/freeze", FreezeAsync);
        group.MapPost("/apartments/{id:guid}/unfreeze", UnfreezeAsync);
        group.MapGet("/apartments/{id:guid}/freezes", ListFreezesAsync).Produces<IReadOnlyList<ApartmentFreezeItem>>();

        return app;
    }

    private static async Task<IResult> ListTowersAsync(AppDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.Towers.AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new TowerItem(t.Id, t.Name, t.SortOrder, t.Apartments.Count))
            .ToListAsync(cancellationToken));

    private static async Task<IResult> CreateTowerAsync(
        CreateTowerRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (await db.Towers.AnyAsync(t => EF.Functions.ILike(t.Name, name), cancellationToken))
        {
            return ApiProblems.Conflict(ErrorCodes.TowerExists);
        }

        var sortOrder = (await db.Towers.MaxAsync(t => (int?)t.SortOrder, cancellationToken) ?? 0) + 1;
        var tower = new Tower(name, sortOrder);
        db.Towers.Add(tower);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.Tower, tower.Id.ToString(), "Created", AuditSource.Admin,
            principal.GetUserId(), time.GetUtcNow(), newValues: AuditLog.Json(new { tower.Name, tower.SortOrder })));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new TowerItem(tower.Id, tower.Name, tower.SortOrder, 0));
    }

    /// <summary>All apartments with their special fee (this month) and how many active residents they have.</summary>
    private static async Task<IResult> ListApartmentsAsync(
        AppDbContext db, TimeProvider time, CondoClock clock, string? search, bool? frozen, CancellationToken cancellationToken)
    {
        var query = db.Apartments.AsNoTracking();
        var term = new string((search ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (term.Length > 0)
        {
            query = query.Where(a => EF.Functions.ILike(a.Code.Replace("-", ""), $"%{term}%"));
        }

        if (frozen is not null)
        {
            query = query.Where(a => a.IsFrozen == frozen);
        }

        var rows = await query
            .OrderBy(a => a.Tower!.SortOrder).ThenBy(a => a.Tower!.Name).ThenBy(a => a.Number)
            .Select(a => new
            {
                a.Id,
                a.Code,
                Tower = a.Tower!.Name,
                a.Number,
                a.IsFrozen,
                Residents = db.Users.Count(u => u.ApartmentId == a.Id && u.IsActive),
            })
            .ToListAsync(cancellationToken);

        var period = clock.CurrentPeriod;
        var apartmentChanges = await db.FeeChanges.AsNoTracking().Where(c => c.Scope == FeeScope.Apartment).ToListAsync(cancellationToken);
        return TypedResults.Ok(rows.Select(r => new ApartmentAdminItem(
            r.Id, r.Code, r.Tower, r.Number, r.IsFrozen, FeeResolver.ApartmentFee(period, r.Id, apartmentChanges), r.Residents)).ToList());
    }

    private static async Task<IResult> CreateApartmentAsync(
        CreateApartmentRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var tower = await db.Towers.SingleOrDefaultAsync(t => t.Id == request.TowerId, cancellationToken);
        if (tower is null)
        {
            return ApiProblems.BadRequest(ErrorCodes.TowerNotFound);
        }

        var code = Apartment.BuildCode(tower.Name, request.Number);
        if (await db.Apartments.AnyAsync(a => a.Code == code, cancellationToken))
        {
            return ApiProblems.Conflict(ErrorCodes.ApartmentExists);
        }

        var apartment = new Apartment(tower, request.Number);
        db.Apartments.Add(apartment);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.Apartment, apartment.Id.ToString(), "Created", AuditSource.Admin,
            principal.GetUserId(), time.GetUtcNow(), newValues: AuditLog.Json(new { apartment.Code }), apartmentId: apartment.Id));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new ApartmentAdminItem(apartment.Id, apartment.Code, tower.Name, apartment.Number, false, null, 0));
    }

    /// <summary>Only an apartment created by mistake can be deleted: no users, codes, fee changes or freezes.</summary>
    private static async Task<IResult> DeleteApartmentAsync(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var apartment = await db.Apartments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (apartment is null)
        {
            return ApiProblems.NotFound(ErrorCodes.ApartmentNotFound);
        }

        var inUse = await db.Users.AnyAsync(u => u.ApartmentId == id, cancellationToken)
            || await db.InvitationCodes.AnyAsync(c => c.ApartmentId == id, cancellationToken)
            || await db.FeeChanges.AnyAsync(c => c.ApartmentId == id, cancellationToken)
            || await db.ApartmentFreezes.AnyAsync(f => f.ApartmentId == id, cancellationToken);
        if (inUse)
        {
            return ApiProblems.Conflict(ErrorCodes.ApartmentInUse);
        }

        db.Apartments.Remove(apartment);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.Apartment, apartment.Id.ToString(), "Deleted", AuditSource.Admin,
            principal.GetUserId(), time.GetUtcNow(), oldValues: AuditLog.Json(new { apartment.Code })));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> PreviewImportAsync(ImportApartmentsRequest request, AppDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ApartmentImport.PreviewAsync(db, request.Csv, cancellationToken));

    private static async Task<IResult> ImportAsync(
        ImportApartmentsRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApartmentListEntry> entries;
        try
        {
            entries = ApartmentList.Parse(request.Csv);
        }
        catch (FormatException)
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidCsv);
        }

        return TypedResults.Ok(await ApartmentImport.ApplyAsync(
            db, entries, AuditSource.Admin, principal.GetUserId(), time.GetUtcNow(), cancellationToken));
    }

    /// <summary>A frozen apartment gets no new charges, late fees or reminders until it is unfrozen.</summary>
    private static async Task<IResult> FreezeAsync(
        Guid id, FreezeApartmentRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var apartment = await db.Apartments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (apartment is null)
        {
            return ApiProblems.NotFound(ErrorCodes.ApartmentNotFound);
        }

        if (apartment.IsFrozen)
        {
            return ApiProblems.Conflict(ErrorCodes.ApartmentAlreadyFrozen);
        }

        var adminId = principal.GetUserId();
        var now = time.GetUtcNow();
        var freeze = apartment.Freeze(request.Reason, adminId, now);
        db.ApartmentFreezes.Add(freeze);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.Apartment, apartment.Id.ToString(), "Frozen", AuditSource.Admin, adminId, now,
            oldValues: AuditLog.Json(new { IsFrozen = false }), newValues: AuditLog.Json(new { IsFrozen = true }),
            reason: freeze.Reason, apartmentId: apartment.Id));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UnfreezeAsync(
        Guid id, FreezeApartmentRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var apartment = await db.Apartments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (apartment is null)
        {
            return ApiProblems.NotFound(ErrorCodes.ApartmentNotFound);
        }

        var freeze = await db.ApartmentFreezes.SingleOrDefaultAsync(f => f.ApartmentId == id && f.EndedAt == null, cancellationToken);
        if (!apartment.IsFrozen || freeze is null)
        {
            return ApiProblems.Conflict(ErrorCodes.ApartmentNotFrozen);
        }

        var adminId = principal.GetUserId();
        var now = time.GetUtcNow();
        apartment.Unfreeze(freeze, request.Reason, adminId, now);
        db.AuditEvents.Add(new AuditEvent(AuditEntities.Apartment, apartment.Id.ToString(), "Unfrozen", AuditSource.Admin, adminId, now,
            oldValues: AuditLog.Json(new { IsFrozen = true }), newValues: AuditLog.Json(new { IsFrozen = false }),
            reason: request.Reason.Trim(), apartmentId: apartment.Id));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListFreezesAsync(Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.ApartmentFreezes.AsNoTracking()
            .Where(f => f.ApartmentId == id)
            .OrderByDescending(f => f.StartedAt)
            .Select(f => new ApartmentFreezeItem(
                f.Id,
                f.StartedAt,
                f.Reason,
                db.Users.Where(u => u.Id == f.StartedById).Select(u => u.FirstName + " " + u.LastName).FirstOrDefault() ?? "",
                f.EndedAt,
                f.EndReason,
                db.Users.Where(u => u.Id == f.EndedById).Select(u => u.FirstName + " " + u.LastName).FirstOrDefault()))
            .ToListAsync(cancellationToken));
}
