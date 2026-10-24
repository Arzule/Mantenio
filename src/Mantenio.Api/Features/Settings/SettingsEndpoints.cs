using System.Globalization;
using System.Security.Claims;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Notifications;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Common;
using Mantenio.Domain.Condo;
using Mantenio.Domain.Fees;
using Mantenio.Domain.Notifications;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Notifications;
using Mantenio.Shared.Settings;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Settings;

internal static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/fees/mine", GetMyFeeAsync).WithTags("Fees").RequireAuthorization().Produces<MyFeeResponse>();

        var admin = app.MapGroup("/api/admin").WithTags("Settings").RequireAuthorization(MantenioPolicies.AdminOnly);
        admin.MapGet("/settings", GetSettingsAsync).Produces<CondoSettingsResponse>();
        admin.MapPut("/settings/due-rule", ChangeDueRuleAsync);
        admin.MapGet("/fees/history", GetFeeHistoryAsync).Produces<IReadOnlyList<FeeChangeItem>>();
        admin.MapPost("/fees/global", ChangeGlobalFeeAsync);
        admin.MapPost("/fees/apartments/{apartmentId:guid}", ChangeApartmentFeeAsync);

        // Only the founder decides the late-fee percentage.
        app.MapPut("/api/admin/settings/late-fee", ChangeLateFeeAsync)
            .WithTags("Settings")
            .RequireAuthorization(MantenioPolicies.FounderOnly);

        return app;
    }

    private static async Task<IResult> GetSettingsAsync(AppDbContext db, TimeProvider time, CondoClock clock, CancellationToken cancellationToken)
    {
        var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, cancellationToken);
        var current = clock.CurrentPeriod;
        var globalChanges = await db.FeeChanges.AsNoTracking().Where(c => c.Scope == FeeScope.Global).ToListAsync(cancellationToken);
        var currentValue = current.Value;
        var scheduled = (await HistoryAsync(db, db.FeeChanges.Where(c => c.Scope == FeeScope.Global && c.EffectivePeriod > currentValue), cancellationToken))
            .OrderBy(c => c.EffectivePeriod)
            .ToList();

        return TypedResults.Ok(new CondoSettingsResponse(
            FeeResolver.GlobalFee(current, settings.MonthlyFee, globalChanges),
            current.Value,
            settings.DueDay,
            settings.DueMonthOffset,
            settings.LateFeePercent,
            settings.TrackingStartPeriod,
            settings.MaxAdmins,
            scheduled));
    }

    /// <summary>The fee of the caller's apartment this month and the next scheduled change, if any.</summary>
    private static async Task<IResult> GetMyFeeAsync(ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CondoClock clock, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var apartment = await db.Users.Where(u => u.Id == userId)
            .Select(u => u.Apartment == null ? null : new { u.Apartment.Id, u.Apartment.Code })
            .SingleOrDefaultAsync(cancellationToken);
        var current = clock.CurrentPeriod;
        if (apartment is null)
        {
            return TypedResults.Ok(new MyFeeResponse(null, null, current.Value, null, null, null));
        }

        var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, cancellationToken);
        var changes = await db.FeeChanges.AsNoTracking()
            .Where(c => c.Scope == FeeScope.Global || c.ApartmentId == apartment.Id)
            .ToListAsync(cancellationToken);
        var currentFee = FeeResolver.Resolve(current, apartment.Id, settings.MonthlyFee, changes);

        // The next month whose fee differs from today's, among the scheduled changes.
        decimal? nextFee = null;
        int? nextPeriod = null;
        foreach (var period in changes.Where(c => c.EffectivePeriod > current.Value).Select(c => c.EffectivePeriod).Distinct().Order())
        {
            var fee = FeeResolver.Resolve(Period.FromValue(period), apartment.Id, settings.MonthlyFee, changes);
            if (fee != currentFee)
            {
                nextFee = fee;
                nextPeriod = period;
                break;
            }
        }

        return TypedResults.Ok(new MyFeeResponse(apartment.Code, currentFee, current.Value, settings.DueDateFor(current), nextFee, nextPeriod));
    }

    private static async Task<IResult> GetFeeHistoryAsync(AppDbContext db, Guid? apartmentId, CancellationToken cancellationToken) =>
        TypedResults.Ok(await HistoryAsync(
            db,
            db.FeeChanges.Where(c => apartmentId == null || c.ApartmentId == apartmentId).OrderByDescending(c => c.CreatedAt).Take(200),
            cancellationToken));

    /// <summary>A new global fee from a future month on (never retroactive). Every active user gets a notice.</summary>
    private static Task<IResult> ChangeGlobalFeeAsync(
        ChangeGlobalFeeRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CondoClock clock,
        NotificationService notifications, CancellationToken cancellationToken) =>
        db.InTransactionAsync<IResult>(async ct =>
        {
            var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, ct);
            if (!IsFutureMonth(request.EffectivePeriod, clock, settings))
            {
                return ApiProblems.BadRequest(ErrorCodes.InvalidPeriod);
            }

            var period = Period.FromValue(request.EffectivePeriod);
            var changes = await db.FeeChanges.Where(c => c.Scope == FeeScope.Global).ToListAsync(ct);
            var oldAmount = FeeResolver.GlobalFee(period, settings.MonthlyFee, changes);
            var adminId = principal.GetUserId();
            var change = FeeChange.Global(oldAmount, request.NewAmount, period, request.Reason, adminId, time.GetUtcNow());
            db.FeeChanges.Add(change);
            db.AuditEvents.Add(new AuditEvent(
                AuditEntities.FeeChange, change.Id.ToString(), "GlobalFeeChanged", AuditSource.Admin, adminId, change.CreatedAt,
                oldValues: AuditLog.Json(new { Amount = oldAmount }),
                newValues: AuditLog.Json(new { Amount = request.NewAmount, change.EffectivePeriod }),
                reason: change.Reason));
            await notifications.NotifyAllActiveUsersAsync(NotificationType.FeeChanged,
                new FeeChangedData(null, oldAmount, request.NewAmount, change.EffectivePeriod, change.Reason), requiresAcknowledgement: false, ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        }, cancellationToken);

    /// <summary>Sets (or with null removes) an apartment's special fee from a future month on.</summary>
    private static Task<IResult> ChangeApartmentFeeAsync(
        Guid apartmentId, ChangeApartmentFeeRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CondoClock clock,
        NotificationService notifications, CancellationToken cancellationToken) =>
        db.InTransactionAsync<IResult>(async ct =>
        {
            var apartment = await db.Apartments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == apartmentId, ct);
            if (apartment is null)
            {
                return ApiProblems.NotFound(ErrorCodes.ApartmentNotFound);
            }

            var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, ct);
            if (!IsFutureMonth(request.EffectivePeriod, clock, settings))
            {
                return ApiProblems.BadRequest(ErrorCodes.InvalidPeriod);
            }

            var period = Period.FromValue(request.EffectivePeriod);
            var changes = await db.FeeChanges.Where(c => c.ApartmentId == apartmentId).ToListAsync(ct);
            var oldAmount = FeeResolver.ApartmentFee(period, apartmentId, changes);
            var adminId = principal.GetUserId();
            var change = FeeChange.ForApartment(apartmentId, oldAmount, request.NewAmount, period, request.Reason, adminId, time.GetUtcNow());
            db.FeeChanges.Add(change);
            db.AuditEvents.Add(new AuditEvent(
                AuditEntities.FeeChange, change.Id.ToString(), "ApartmentFeeChanged", AuditSource.Admin, adminId, change.CreatedAt,
                oldValues: AuditLog.Json(new { SpecialFee = oldAmount }),
                newValues: AuditLog.Json(new { SpecialFee = request.NewAmount, change.EffectivePeriod }),
                reason: change.Reason,
                apartmentId: apartmentId));
            await notifications.NotifyApartmentAsync(apartmentId, NotificationType.FeeChanged,
                new FeeChangedData(apartment.Code, oldAmount, request.NewAmount, change.EffectivePeriod, change.Reason), ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        }, cancellationToken);

    private static async Task<IResult> ChangeDueRuleAsync(
        ChangeDueRuleRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, cancellationToken);
        var old = new { settings.DueDay, settings.DueMonthOffset };
        settings.SetDueRule(request.DueDay, request.DueMonthOffset);
        AddSettingsAudit(db, principal, time, "DueRuleChanged", old, new { settings.DueDay, settings.DueMonthOffset }, request.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ChangeLateFeeAsync(
        ChangeLateFeeRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, cancellationToken);
        var old = new { settings.LateFeePercent };
        settings.SetLateFeePercent(request.Percent);
        AddSettingsAudit(db, principal, time, "LateFeeChanged", old, new { settings.LateFeePercent }, request.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static void AddSettingsAudit(AppDbContext db, ClaimsPrincipal principal, TimeProvider time, string action, object old, object @new, string reason) =>
        db.AuditEvents.Add(new AuditEvent(
            AuditEntities.CondoSettings, CondoSettings.SingletonId.ToString(CultureInfo.InvariantCulture), action, AuditSource.Admin,
            principal.GetUserId(), time.GetUtcNow(),
            oldValues: AuditLog.Json(old), newValues: AuditLog.Json(@new), reason: reason.Trim()));

    /// <summary>Fee changes start next month at the earliest, and never before the system starts tracking.</summary>
    private static bool IsFutureMonth(int period, CondoClock clock, CondoSettings settings) =>
        PeriodValues.IsValid(period)
        && period >= clock.CurrentPeriod.Next().Value
        && period >= settings.TrackingStartPeriod;

    private static async Task<List<FeeChangeItem>> HistoryAsync(AppDbContext db, IQueryable<FeeChange> source, CancellationToken cancellationToken)
    {
        var changes = await source.AsNoTracking().ToListAsync(cancellationToken);
        var adminIds = changes.Select(c => c.ChangedById).Distinct().ToList();
        var apartmentIds = changes.Where(c => c.ApartmentId != null).Select(c => c.ApartmentId!.Value).Distinct().ToList();
        var names = await db.Users.Where(u => adminIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, cancellationToken);
        var codes = await db.Apartments.Where(a => apartmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code, cancellationToken);
        return changes.Select(c => new FeeChangeItem(
            c.Id,
            c.Scope.ToString(),
            c.ApartmentId,
            c.ApartmentId is { } apartmentId ? codes.GetValueOrDefault(apartmentId) : null,
            c.OldAmount,
            c.NewAmount,
            c.EffectivePeriod,
            c.Reason,
            names.GetValueOrDefault(c.ChangedById, ""),
            c.CreatedAt)).ToList();
    }
}
