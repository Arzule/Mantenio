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

    /// <summary>Switches the context to the owner login before migrating, when one is configured.</summary>
    public static void Use(AppDbContext db, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration[SettingName] is { Length: > 0 } owner)
        {
            db.Database.SetConnectionString(owner);
        }
    }
}
