using System.Security.Claims;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Shared.Apartments;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Apartments;

internal static class ApartmentEndpoints
{
    public const int MaxOptions = 50;

    public static IEndpointRouteBuilder MapApartmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/apartments").WithTags("Apartments");

        // Public: the registration form needs it before the person has an account. Codes like "H-102" are not sensitive.
        group.MapGet("/options", SearchOptionsAsync).AllowAnonymous().Produces<IReadOnlyList<ApartmentOption>>();
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization().Produces<ApartmentDetails>();

        return app;
    }

    /// <summary>Text search for the apartment combobox. "h102", "H-102" and "h 102" all match H-102.</summary>
    private static async Task<IResult> SearchOptionsAsync(AppDbContext db, string? search, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 20, 1, MaxOptions);
        var query = db.Apartments.AsNoTracking();

        var term = new string((search ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (term.Length > 0)
        {
            var pattern = $"%{term}%";
            query = query.Where(a => EF.Functions.ILike(a.Code.Replace("-", ""), pattern));
        }

        var options = await query
            .OrderBy(a => a.Tower!.SortOrder).ThenBy(a => a.Tower!.Name).ThenBy(a => a.Number)
            .Take(take)
            .Select(a => new ApartmentOption(a.Id, a.Code, a.Tower!.Name, a.Number))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(options);
    }

    /// <summary>Admins see any apartment; a resident only their own (others answer 404, like a missing id).</summary>
    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await ApartmentAccess.CanAccessAsync(db, principal, id, cancellationToken))
        {
            return ApiProblems.NotFound();
        }

        var details = await db.Apartments.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new ApartmentDetails(
                a.Id,
                a.Code,
                a.Tower!.Name,
                a.Number,
                db.Users
                    .Where(u => u.ApartmentId == a.Id && u.IsActive)
                    .OrderBy(u => u.LastName).ThenBy(u => u.FirstName)
                    .Select(u => new ApartmentResident(u.Id, u.FirstName, u.LastName))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);
        return details is null ? ApiProblems.NotFound() : TypedResults.Ok(details);
    }
}

internal static class ApartmentQueries
{
    public static Task<ApartmentOption?> FindOptionAsync(AppDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Apartments.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new ApartmentOption(a.Id, a.Code, a.Tower!.Name, a.Number))
            .SingleOrDefaultAsync(cancellationToken);
}

/// <summary>
/// Ownership check for every resident resource (IDOR guard). Reads the apartment from the database, not the token,
/// so a change of apartment by an admin applies immediately.
/// </summary>
internal static class ApartmentAccess
{
    public static async Task<bool> CanAccessAsync(AppDbContext db, ClaimsPrincipal principal, Guid apartmentId, CancellationToken cancellationToken)
    {
        if (principal.IsAdmin())
        {
            return true;
        }

        var userId = principal.GetUserId();
        return await db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.ApartmentId == apartmentId, cancellationToken);
    }
}
