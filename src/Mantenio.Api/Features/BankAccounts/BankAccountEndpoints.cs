using System.Security.Claims;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Notifications;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Banking;
using Mantenio.Domain.Notifications;
using Mantenio.Shared.Auth;
using Mantenio.Shared.BankAccounts;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.BankAccounts;

/// <summary>
/// Condo bank accounts. Any change notifies every active user with a notice they must acknowledge (popup at app
/// start; push in Phase 6), because "the account changed" is the classic fraud: never by email.
/// </summary>
internal static class BankAccountEndpoints
{
    public static IEndpointRouteBuilder MapBankAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bank-accounts", ListActiveAsync).WithTags("BankAccounts").RequireAuthorization()
            .Produces<IReadOnlyList<BankAccountItem>>();

        var admin = app.MapGroup("/api/admin/bank-accounts").WithTags("BankAccounts").RequireAuthorization(MantenioPolicies.AdminOnly);
        admin.MapGet("/", ListAllAsync).Produces<IReadOnlyList<BankAccountItem>>();
        admin.MapPost("/", CreateAsync).Produces<BankAccountItem>();
        admin.MapPut("/{id:guid}", UpdateAsync).Produces<BankAccountItem>();
        admin.MapPost("/{id:guid}/deactivate", DeactivateAsync);

        return app;
    }

    private static async Task<IResult> ListActiveAsync(AppDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await Project(db.BankAccounts.Where(b => b.IsActive)).ToListAsync(cancellationToken));

    private static async Task<IResult> ListAllAsync(AppDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await Project(db.BankAccounts).ToListAsync(cancellationToken));

    private static Task<IResult> CreateAsync(
        SaveBankAccountRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time,
        NotificationService notifications, CancellationToken cancellationToken) =>
        db.InTransactionAsync<IResult>(async ct =>
        {
            if (!IsValid(request))
            {
                return ApiProblems.BadRequest(ErrorCodes.InvalidBankAccount);
            }

            var adminId = principal.GetUserId();
            var now = time.GetUtcNow();
            var account = new BankAccount(request.BankName, request.HolderName, request.AccountNumber, request.Cci, request.SortOrder, adminId, now);
            db.BankAccounts.Add(account);
            db.AuditEvents.Add(new AuditEvent(AuditEntities.BankAccount, account.Id.ToString(), "Created", AuditSource.Admin, adminId, now,
                newValues: Snapshot(account), reason: request.Reason.Trim()));
            await notifications.NotifyAllActiveUsersAsync(NotificationType.BankAccountsChanged,
                new BankAccountsChangedData(BankAccountChanges.Added, account.BankName, request.Reason.Trim()), requiresAcknowledgement: true, ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToItem(account));
        }, cancellationToken);

    private static Task<IResult> UpdateAsync(
        Guid id, SaveBankAccountRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time,
        NotificationService notifications, CancellationToken cancellationToken) =>
        db.InTransactionAsync<IResult>(async ct =>
        {
            var account = await db.BankAccounts.SingleOrDefaultAsync(b => b.Id == id, ct);
            if (account is null)
            {
                return ApiProblems.NotFound();
            }

            if (!IsValid(request))
            {
                return ApiProblems.BadRequest(ErrorCodes.InvalidBankAccount);
            }

            var old = Snapshot(account);
            var adminId = principal.GetUserId();
            var now = time.GetUtcNow();
            account.Update(request.BankName, request.HolderName, request.AccountNumber, request.Cci, request.SortOrder, adminId, now);
            var changed = Snapshot(account);
            if (changed == old)
            {
                return TypedResults.Ok(ToItem(account));
            }

            db.AuditEvents.Add(new AuditEvent(AuditEntities.BankAccount, account.Id.ToString(), "Updated", AuditSource.Admin, adminId, now,
                oldValues: old, newValues: changed, reason: request.Reason.Trim()));
            await notifications.NotifyAllActiveUsersAsync(NotificationType.BankAccountsChanged,
                new BankAccountsChangedData(BankAccountChanges.Updated, account.BankName, request.Reason.Trim()), requiresAcknowledgement: true, ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToItem(account));
        }, cancellationToken);

    private static Task<IResult> DeactivateAsync(
        Guid id, DeactivateBankAccountRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time,
        NotificationService notifications, CancellationToken cancellationToken) =>
        db.InTransactionAsync<IResult>(async ct =>
        {
            var account = await db.BankAccounts.SingleOrDefaultAsync(b => b.Id == id, ct);
            if (account is null)
            {
                return ApiProblems.NotFound();
            }

            if (account.IsActive)
            {
                var adminId = principal.GetUserId();
                var now = time.GetUtcNow();
                account.Deactivate(adminId, now);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.BankAccount, account.Id.ToString(), "Deactivated", AuditSource.Admin, adminId, now,
                    oldValues: AuditLog.Json(new { IsActive = true }), newValues: AuditLog.Json(new { IsActive = false }), reason: request.Reason.Trim()));
                await notifications.NotifyAllActiveUsersAsync(NotificationType.BankAccountsChanged,
                    new BankAccountsChangedData(BankAccountChanges.Removed, account.BankName, request.Reason.Trim()), requiresAcknowledgement: true, ct);
                await db.SaveChangesAsync(ct);
            }

            return TypedResults.NoContent();
        }, cancellationToken);

    private static bool IsValid(SaveBankAccountRequest request) =>
        !string.IsNullOrWhiteSpace(request.BankName)
        && !string.IsNullOrWhiteSpace(request.HolderName)
        && BankAccount.IsValidAccountNumber(BankAccount.NormalizeAccountNumber(request.AccountNumber))
        && BankAccount.IsValidCci(BankAccount.NormalizeCci(request.Cci));

    private static string Snapshot(BankAccount account) =>
        AuditLog.Json(new { account.BankName, account.HolderName, account.AccountNumber, account.Cci, account.SortOrder });

    private static BankAccountItem ToItem(BankAccount b) =>
        new(b.Id, b.BankName, b.HolderName, b.AccountNumber, b.Cci, b.IsActive, b.SortOrder, b.UpdatedAt);

    private static IQueryable<BankAccountItem> Project(IQueryable<BankAccount> source) =>
        source.AsNoTracking()
            .OrderByDescending(b => b.IsActive).ThenBy(b => b.SortOrder).ThenBy(b => b.BankName)
            .Select(b => new BankAccountItem(b.Id, b.BankName, b.HolderName, b.AccountNumber, b.Cci, b.IsActive, b.SortOrder, b.UpdatedAt));
}
