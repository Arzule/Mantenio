using Mantenio.Shared.Condos;
using MudBlazor;

namespace Mantenio.UI.Theme;

/// <summary>Builds the MudBlazor theme from the condo profile colors. Layouts never change; only tokens do.</summary>
public static class CondoThemeFactory
{
    public static MudTheme Create(CondoProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var colors = profile.Theme;

        var palette = new PaletteLight
        {
            Primary = colors.Primary,
            Secondary = colors.Secondary,
            AppbarBackground = colors.AppBar ?? colors.Primary,
            AppbarText = "#FFFFFF",
            DrawerBackground = colors.Surface ?? "#FFFFFF",
        };

        if (colors.PrimaryDarken is not null) palette.PrimaryDarken = colors.PrimaryDarken;
        if (colors.Tertiary is not null) palette.Tertiary = colors.Tertiary;
        if (colors.Background is not null) palette.Background = colors.Background;
        if (colors.Surface is not null) palette.Surface = colors.Surface;
        if (colors.TextPrimary is not null)
        {
            palette.TextPrimary = colors.TextPrimary;
            palette.DrawerText = colors.TextPrimary;
        }

        return new MudTheme
        {
            PaletteLight = palette,
            Typography = new Typography
            {
                Default = new DefaultTypography
                {
                    FontFamily = ["Roboto", "Segoe UI", "Helvetica", "Arial", "sans-serif"],
                },
            },
            LayoutProperties = new LayoutProperties
            {
                DefaultBorderRadius = "8px",
            },
        };
    }
}
