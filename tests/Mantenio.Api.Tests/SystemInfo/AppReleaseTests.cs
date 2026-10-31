using System.Net;
using System.Net.Http.Json;
using System.Text;
using Mantenio.Api.Features.AppRelease;
using Mantenio.Api.Infrastructure.Notifications;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Domain.Notifications;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Notifications;
using Mantenio.Shared.SystemInfo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.SystemInfo;

/// <summary>The published Android version, its stable download link and the one-time announcement.</summary>
public sealed class AppReleaseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_published_release_is_offered_and_downloaded_through_a_stable_link()
    {
        using var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await anonymous.GetAsync(new Uri("/api/app/android/version", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await PublishAsync(new AndroidReleaseFile(105, "1.0.5", 104, "app/android/El-Prado-1.0.5.apk", DateTimeOffset.UtcNow));

        var release = await anonymous.GetFromJsonAsync<AndroidRelease>("/api/app/android/version", Ct);
        release.ShouldBe(new AndroidRelease(105, "1.0.5", 104, "/api/app/android"));

        var download = await anonymous.GetAsync(new Uri("/api/app/android", UriKind.Relative), Ct);
        download.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        download.Headers.Location!.AbsoluteUri.ShouldContain("app/android/El-Prado-1.0.5.apk");
    }

    [Fact]
    public async Task Each_release_is_announced_once_to_the_users_with_the_app_on_a_phone()
    {
        using var phone = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, "phone@test.local", "C-401"));
        await factory.RegisterAsync(AccountRole.Resident, "webonly@test.local", "C-402");
        await (await phone.PostAsJsonAsync("/api/devices", new RegisterDeviceRequest("token-release", "android"), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);
        var release = new AndroidReleaseFile(200, "2.0.0", 0, "app/android/x.apk", null);

        (await AnnounceAsync(release)).ShouldBeTrue();
        (await AnnounceAsync(release)).ShouldBeFalse();

        var notices = await factory.WithDbAsync(db => db.Notifications
            .Where(n => n.Type == NotificationType.AppUpdateAvailable)
            .Join(db.Users, n => n.UserId, u => u.Id, (n, u) => u.Email)
            .ToListAsync(Ct));
        notices.ShouldBe(["phone@test.local"]);
    }

    private async Task<bool> AnnounceAsync(AndroidReleaseFile release)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        return await AppReleaseService.AnnounceAsync(services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<NotificationService>(), release, TimeProvider.System, Ct);
    }

    private async Task PublishAsync(AndroidReleaseFile release)
    {
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(AuditLog.Json(release)));
        await storage.PutAsync(StorageKeys.AndroidRelease, json, "application/json", Ct);
        factory.Services.GetRequiredService<AppReleaseService>().Forget();
    }
}
