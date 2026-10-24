using Mantenio.UI.Resources;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;

namespace Mantenio.UI.Layout;

public sealed record NavigationItem(string Href, string Title, string Icon, NavLinkMatch Match = NavLinkMatch.Prefix);

/// <summary>Single source for the side drawer (desktop) and the bottom bar (mobile). Phases add items here.</summary>
public static class NavigationItems
{
    public static IReadOnlyList<NavigationItem> All { get; } =
    [
        new("", UiStrings.NavHome, Icons.Material.Filled.Home, NavLinkMatch.All),
    ];
}
