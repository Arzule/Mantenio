using System.Globalization;
using Mantenio.Client;
using Mantenio.Client.Auth;
using Mantenio.Shared.Condos;
using Mantenio.UI.Platform;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace Mantenio.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddMudServices();
        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<IFileOpener, LauncherFileOpener>();
#if ANDROID
        builder.Services.AddSingleton<IPushRegistration, AndroidPushRegistration>();
#else
        builder.Services.AddSingleton<IPushRegistration, NoPushRegistration>();
#endif
        builder.Services.AddSingleton(new ClientPreferences(KeepSignedInByDefault: true));
        builder.Services.AddMantenioClient(ApiBaseAddress());

#if DEBUG
#if ANDROID
        // Debug only: screenshots come from http://localhost:5013 (adb reverse) into the https app origin.
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("MixedContent", (handler, _) =>
            handler.PlatformView.Settings.MixedContentMode = Android.Webkit.MixedContentHandling.AlwaysAllow);
#endif
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    /// <summary>
    /// Debug builds talk to the API on the dev machine through `adb reverse tcp:5013 tcp:5013`
    /// (works the same on a USB phone and on the emulator). Release builds use the condo profile.
    /// </summary>
    private static Uri ApiBaseAddress()
    {
#if DEBUG
        return new Uri("http://localhost:5013");
#else
        // Filled in condo.json when the API is deployed (Phase 2).
        return new Uri(CondoProfile.Current.ApiBaseUrl is { Length: > 0 } url ? url : "https://localhost/");
#endif
    }
}
