using Bunit;
using Mantenio.Client;
using Mantenio.Shared.SystemInfo;
using Mantenio.UI.Components;
using Mantenio.UI.Platform;
using Mantenio.UI.Resources;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Shouldly;

namespace Mantenio.UI.Tests;

/// <summary>The web shows a QR and a link to the published Android app; the app itself does not.</summary>
public sealed class AndroidDownloadCardTests : BunitContext
{
    private readonly FakeApi _api = new();

    public AndroidDownloadCardTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(_api);
        Services.AddSingleton<PublicApi>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void The_web_links_to_the_published_apk_with_a_qr_code()
    {
        Services.AddSingleton<IAppUpdater, NoAppUpdater>();
        _api.Returns("/api/app/android/version", new AndroidRelease(106, "1.0.5", 0, "/api/app/android"));

        var cut = Render<AndroidDownloadCard>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(UiStrings.AndroidDownload));
        cut.Find("a").GetAttribute("href").ShouldBe("http://api.test/api/app/android");
        cut.Find("svg path").GetAttribute("d").ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Nothing_without_a_published_release()
    {
        Services.AddSingleton<IAppUpdater, NoAppUpdater>();
        Render<AndroidDownloadCard>().Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public void The_android_app_does_not_offer_itself()
    {
        Services.AddSingleton<IAppUpdater>(new InstalledApp());
        _api.Returns("/api/app/android/version", new AndroidRelease(106, "1.0.5", 0, "/api/app/android"));

        Render<AndroidDownloadCard>().Markup.Trim().ShouldBeEmpty();
        _api.Calls.ShouldBeEmpty();
    }

    private sealed class InstalledApp : IAppUpdater
    {
        public int? InstalledVersionCode => 106;

        public Task InstallAsync(Uri apk, IProgress<double>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
