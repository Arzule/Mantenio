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

/// <summary>The update offer on Android: required below the minimum version, dismissable when merely older.</summary>
public sealed class AppUpdateGateTests : BunitContext
{
    private readonly FakeApi _api = new();
    private readonly FakeUpdater _updater = new();

    public AppUpdateGateTests()
    {
        AppUpdateGate.ResetSession();
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(_api);
        Services.AddSingleton<PublicApi>();
        Services.AddSingleton<IAppUpdater>(_updater);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void An_older_app_gets_an_offer_it_can_put_off()
    {
        _updater.Installed = 104;
        _api.Returns("/api/app/android/version", new AndroidRelease(105, "1.0.5", 0, "/api/app/android"));

        var cut = Render<AppUpdateGate>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(UiStrings.UpdateAvailableTitle));
        cut.FindAll("button").Single(b => b.TextContent.Contains(UiStrings.UpdateLater, StringComparison.Ordinal)).Click();
        cut.Markup.ShouldNotContain(UiStrings.UpdateAvailableTitle);
    }

    [Fact]
    public void Below_the_minimum_the_app_must_update_and_the_download_starts()
    {
        _updater.Installed = 100;
        _api.Returns("/api/app/android/version", new AndroidRelease(105, "1.0.5", 104, "/api/app/android"));

        var cut = Render<AppUpdateGate>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(UiStrings.UpdateRequiredTitle));
        cut.Markup.ShouldNotContain(UiStrings.UpdateLater);
        cut.FindAll("button").Single(b => b.TextContent.Contains(UiStrings.UpdateNow, StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => _updater.Downloaded.ShouldBe(new Uri("http://api.test/api/app/android")));
    }

    [Fact]
    public void The_latest_version_and_the_web_see_nothing()
    {
        _updater.Installed = 105;
        _api.Returns("/api/app/android/version", new AndroidRelease(105, "1.0.5", 104, "/api/app/android"));
        Render<AppUpdateGate>().Markup.Trim().ShouldBeEmpty();

        AppUpdateGate.ResetSession();
        _updater.Installed = null;
        Render<AppUpdateGate>().Markup.Trim().ShouldBeEmpty();
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public int? Installed { get; set; }

        public Uri? Downloaded { get; private set; }

        public int? InstalledVersionCode => Installed;

        public Task InstallAsync(Uri apk, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Downloaded = apk;
            return Task.CompletedTask;
        }
    }
}
