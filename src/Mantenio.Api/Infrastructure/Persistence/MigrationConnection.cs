using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Infrastructure.Persistence;

/// <summary>
/// In production the app runs with a login that cannot drop tables, disable triggers or delete ledger rows; only the
/// migrations use the owner login ("Database:MigrationConnectionString"). Without it (development, tests) the normal
/// connection is used.
/// </summary>
internal static class MigrationConnection
{
    public const string SettingName = "Database:MigrationConnectionString";

    /// <summary>
    /// Applies the migrations with the owner login when one is configured. It uses a context of its own: the app's
    /// contexts are pooled, and changing the connection of one would leak the owner login to later requests.
    /// </summary>
    public static async Task MigrateAsync(AppDbContext appDb, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appDb);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration[SettingName] is not { Length: > 0 } owner)
        {
            await appDb.Database.MigrateAsync(cancellationToken);
            return;
        }

        await using var migrating = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(owner).Options);
        await migrating.Database.MigrateAsync(cancellationToken);
    }
}
