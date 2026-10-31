using System.Text.Json;
using Mantenio.Api.Infrastructure.Notifications;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Mantenio.Api.Features.AppRelease;

/// <summary>
/// The Android release published by deploy/build_android.py --publish: <c>app/android/version.json</c> in the files
/// bucket, next to the APK. <see cref="MinVersionCode"/> rises only when a release is marked as required (older apps
/// no longer work with the server).
/// </summary>
public sealed record AndroidReleaseFile(int VersionCode, string VersionName, int MinVersionCode, string ApkKey, DateTimeOffset? PublishedAt);

/// <summary>"AppRelease" section.</summary>
public sealed class AppReleaseOptions
{
    public const string SectionName = "AppRelease";

    /// <summary>Background check that announces a new release once (off in tests, which call it directly).</summary>
    public bool Watch { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Reads the published release (cached for a minute) and announces each new one exactly once.</summary>
public sealed class AppReleaseService(IFileStorage storage, IMemoryCache cache)
{
    private const string CacheKey = "android-release";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A short advisory-lock key so two replicas never announce the same release twice.</summary>
    private const long LockKey = 0x4D414E54_415050;

    public async Task<AndroidReleaseFile?> GetAsync(CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var bytes = await storage.GetAsync(StorageKeys.AndroidRelease, cancellationToken);
            if (bytes is null)
            {
                return null;
            }

            var release = JsonSerializer.Deserialize<AndroidReleaseFile>(bytes, Json);
            return release is { VersionCode: > 0, ApkKey.Length: > 0 } ? release : null;
        });

    public void Forget() => cache.Remove(CacheKey);

    /// <summary>
    /// Tells the users who have the Android app (a registered phone) that a new version is out: one in-app notice and
    /// one push per release, never again (an audit event per version code marks it as announced).
    /// </summary>
    public static Task<bool> AnnounceAsync(
        AppDbContext db, NotificationService notifications, AndroidReleaseFile release, TimeProvider time, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct);
            var key = release.VersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (await db.AuditEvents.AnyAsync(e => e.EntityType == AuditEntities.AppRelease && e.EntityId == key, ct))
            {
                return false;
            }

            var count = await notifications.NotifyDeviceUsersAsync(NotificationType.AppUpdateAvailable,
                new { release.VersionName, release.VersionCode, Required = release.MinVersionCode >= release.VersionCode }, ct);
            db.AuditEvents.Add(new AuditEvent(AuditEntities.AppRelease, key, "Announced", AuditSource.System, null, time.GetUtcNow(),
                newValues: AuditLog.Json(new { release.VersionName, release.VersionCode, release.MinVersionCode, Users = count })));
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
}

/// <summary>Checks for a newly published release every few minutes and announces it (see <see cref="AppReleaseService.AnnounceAsync"/>).</summary>
public sealed partial class AppReleaseWatcher(IServiceScopeFactory scopes, IOptions<AppReleaseOptions> options, ILogger<AppReleaseWatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Watch)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var releases = scope.ServiceProvider.GetRequiredService<AppReleaseService>();
                releases.Forget();
                if (await releases.GetAsync(stoppingToken) is { } release)
                {
                    await AppReleaseService.AnnounceAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                        scope.ServiceProvider.GetRequiredService<NotificationService>(), release, TimeProvider.System, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Android release check failed")]
    private partial void LogCheckFailed(Exception exception);
}
