using System.Globalization;
using Mantenio.Shared.Condos;
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

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
