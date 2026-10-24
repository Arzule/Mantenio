using System.Security.Claims;
using Mantenio.Shared.Auth;
using Mantenio.UI.Resources;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;

namespace Mantenio.UI.Layout;

public sealed record NavigationItem(string Href, string Title, string Icon, NavLinkMatch Match = NavLinkMatch.Prefix, bool AdminOnly = false);

/// <summary>Single source for the side drawer (desktop) and the bottom bar (mobile). Phases add items here.</summary>
public static class NavigationItems
{
    public static IReadOnlyList<NavigationItem> All { get; } =
    [
        new("", UiStrings.NavHome, Icons.Material.Filled.Home, NavLinkMatch.All),
        new("admin/users", UiStrings.NavUsers, Icons.Material.Filled.People, AdminOnly: true),
        new("admin/invitations", UiStrings.NavInvitations, Icons.Material.Filled.PersonAdd, AdminOnly: true),
    ];

    public static IEnumerable<NavigationItem> For(ClaimsPrincipal user)
    {
        var isAdmin = user.HasClaim(MantenioClaims.Role, nameof(AccountRole.Admin));
        return All.Where(item => !item.AdminOnly || isAdmin);
    }
}
