using System.Reflection;
using System.Security.Claims;
using Bunit;
using Mantenio.Client;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Errors;
using Mantenio.UI.Errors;
using Mantenio.UI.Layout;
using Mantenio.UI.Pages;
using Mantenio.UI.Resources;
using Mantenio.UI.Theme;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Shouldly;

namespace Mantenio.UI.Tests;

public sealed class UiTests : BunitContext
{
    public UiTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new FakeApi()
            .Returns("/api/fees/mine", new Mantenio.Shared.Settings.MyFeeResponse("H-102", 180m, 202611, new DateOnly(2026, 12, 1), null, null)));
        Services.AddSingleton<MantenioApi>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Home_greets_the_signed_in_user_and_shows_the_condo_name()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("Rosa Quispe");
        auth.SetClaims(new Claim(MantenioClaims.FirstName, "Rosa"));

        var cut = Render<Home>();

        cut.Find("h1").TextContent.ShouldContain(CondoProfile.Current.DisplayName);
        cut.Markup.ShouldContain(UiStrings.HomeGreeting.Replace("{0}", "Rosa", StringComparison.Ordinal));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("S/"));
        cut.Markup.ShouldContain("180.00");
        cut.Markup.ShouldContain("01/12/2026");
    }

    [Fact]
    public void Theme_uses_the_profile_colors()
    {
        var theme = CondoThemeFactory.Create(CondoProfile.Current);

        theme.PaletteLight.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex)
            .ShouldBe(CondoProfile.Current.Theme.Primary, StringCompareShould.IgnoreCase);
        theme.PaletteLight.Secondary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex)
            .ShouldBe(CondoProfile.Current.Theme.Secondary, StringCompareShould.IgnoreCase);
    }

    public static TheoryData<string> AllErrorCodes()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            data.Add((string)field.GetRawConstantValue()!);
        }

        data.Add(ApiException.NetworkUnavailable);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllErrorCodes))]
    public void Every_api_error_code_has_a_spanish_message(string code) =>
        UiStrings.ResourceManager.GetString("Error_" + code, UiStrings.Culture).ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void Unknown_error_codes_fall_back_to_the_generic_message() =>
        ErrorMessages.For("something_new").ShouldBe(UiStrings.Error_internal_error);

    [Fact]
    public void Residents_do_not_see_the_admin_menu()
    {
        var resident = new ClaimsPrincipal(new ClaimsIdentity([new Claim(MantenioClaims.Role, nameof(AccountRole.Resident))], "jwt"));
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(MantenioClaims.Role, nameof(AccountRole.Admin))], "jwt"));

        NavigationItems.For(resident).ShouldAllBe(item => !item.AdminOnly);
        NavigationItems.For(admin).Count().ShouldBe(NavigationItems.All.Count);
    }
}
