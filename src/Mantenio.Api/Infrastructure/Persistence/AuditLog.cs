using System.Security.Claims;
using System.Text.Json;
using Mantenio.Api.Infrastructure.Auth;
using Mantenio.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Infrastructure.Persistence;

internal static class AuditEntities
{
    public const string User = "User";
    public const string InvitationCode = "InvitationCode";
    public const string ApartmentList = "ApartmentList";
}

internal static class AuditLog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Json(object value) => JsonSerializer.Serialize(value, Options);

    public static AuditSource SourceFor(ClaimsPrincipal user) => user.IsAdmin() ? AuditSource.Admin : AuditSource.Resident;
}

internal static class TransactionExtensions
{
    /// <summary>
    /// Runs the work in one database transaction inside the retrying execution strategy.
    /// The work must not save anything before it decides to fail, so committing is always safe.
    /// </summary>
    public static Task<T> InTransactionAsync<T>(this AppDbContext db, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(
            work,
            static async (context, work, ct) =>
            {
                var appDb = (AppDbContext)context;
                await using var transaction = await appDb.Database.BeginTransactionAsync(ct);
                var result = await work(ct);
                await transaction.CommitAsync(ct);
                return result;
            },
            verifySucceeded: null,
            cancellationToken);
}
