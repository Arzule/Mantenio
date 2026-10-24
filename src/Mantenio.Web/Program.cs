using System.Globalization;
using Mantenio.Client;
using Mantenio.Client.Auth;
using Mantenio.Shared.Condos;
using Mantenio.UI.Platform;
using Mantenio.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// API address: wwwroot/appsettings.{Environment}.json for local runs, otherwise the condo profile.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] is { Length: > 0 } configured ? configured
    : CondoProfile.Current.ApiBaseUrl is { Length: > 0 } profile ? profile
    : builder.HostEnvironment.BaseAddress;

builder.Services.AddMudServices();
builder.Services.AddSingleton<ITokenStore, BrowserTokenStore>();
builder.Services.AddSingleton<IFileOpener, BrowserFileOpener>();
builder.Services.AddSingleton(new ClientPreferences(KeepSignedInByDefault: false));
builder.Services.AddMantenioClient(new Uri(apiBaseUrl));

await builder.Build().RunAsync();
