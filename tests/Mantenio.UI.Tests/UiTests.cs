using System.Reflection;
using Bunit;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Errors;
using Mantenio.UI.Errors;
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
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Home_shows_the_condo_name_from_the_profile()
    {
        var cut = Render<Home>();

        cut.Find("h1").TextContent.ShouldContain(CondoProfile.Current.DisplayName);
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
        return data;
    }

    [Theory]
    [MemberData(nameof(AllErrorCodes))]
    public void Every_api_error_code_has_a_spanish_message(string code) =>
        UiStrings.ResourceManager.GetString("Error_" + code, UiStrings.Culture).ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void Unknown_error_codes_fall_back_to_the_generic_message() =>
        ErrorMessages.For("something_new").ShouldBe(UiStrings.Error_internal_error);
}
