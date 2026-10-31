using System.Security.Claims;
using Mantenio.Shared.Auth;
using Mantenio.UI.Resources;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;

namespace Mantenio.UI.Layout;

/// <summary>Who sees a menu entry.</summary>
public enum NavAudience
{
    Everyone,

    /// <summary>Residents and admins: people with an apartment (the gatehouse has none).</summary>
    ApartmentHolders,
    Admin,

    /// <summary>The gatehouse (portería) only; admins reach the same pages from their own entries.</summary>
    Guard,
}

/// <param name="InBottomBar">Shown in the phone bottom bar (at most 4 items).</param>
/// <param name="InDrawer">Shown in the desktop side drawer.</param>
public sealed record NavigationItem(
    string Href,
    string Title,
    string Icon,
    NavLinkMatch Match = NavLinkMatch.Prefix,
    NavAudience Audience = NavAudience.Everyone,
    bool InBottomBar = true,
    bool InDrawer = true)
{
    public bool AdminOnly => Audience == NavAudience.Admin;
}

/// <summary>Single source for the side drawer (desktop) and the bottom bar (mobile). Phases add items here.</summary>
public static class NavigationItems
{
    public static IReadOnlyList<NavigationItem> All { get; } =
    [
        new("", UiStrings.NavHome, Icons.Material.Filled.Home, NavLinkMatch.All),
        new("payments", UiStrings.NavPayments, Icons.Material.Filled.ReceiptLong, Audience: NavAudience.ApartmentHolders),
        new("admin/payments", UiStrings.NavPaymentReview, Icons.Material.Filled.FactCheck, Audience: NavAudience.Guard),
        new("admin/invitations", UiStrings.NavInvitations, Icons.Material.Filled.PersonAdd, Audience: NavAudience.Guard),

        // Also linked from the payments page ("Ver cuentas para pagar"), so it leaves room in the bottom bar.
        new("bank-accounts", UiStrings.NavBankAccounts, Icons.Material.Filled.AccountBalance, InBottomBar: false),
        new("notifications", UiStrings.NavNotifications, Icons.Material.Filled.Notifications),
        new("help", UiStrings.NavHelp, Icons.Material.Filled.HelpOutline, InBottomBar: false),

        // Phones get one "Administración" entry (a hub page with the dashboard); the desktop drawer also lists every admin page.
        new("admin", UiStrings.NavAdmin, Icons.Material.Filled.AdminPanelSettings, NavLinkMatch.All, NavAudience.Admin),
        new("admin/payments", UiStrings.NavPaymentReview, Icons.Material.Filled.FactCheck, Audience: NavAudience.Admin, InBottomBar: false),
        new("admin/reports", UiStrings.NavReports, Icons.Material.Filled.Assessment, Audience: NavAudience.Admin, InBottomBar: false),
        new("admin/audit", UiStrings.NavAudit, Icons.Material.Filled.History, Audience: NavAudience.Admin, InBottomBar: false),
        new("admin/charges", UiStrings.NavCharges, Icons.Material.Filled.RequestQuote, Audience: NavAudience.Admin, InBottomBar: false),
        new("admin/users", UiStrings.NavUsers, Icons.Material.Filled.People, Audience: NavAudience.Admin, InBottomBar: false),
        new("admin/invitations", UiStrings.NavInvitations, Icons.Material.Filled.PersonAdd, Audience: NavAudience.Admin, InBottomBar: false),
        new("admin/apartments", UiStrings.NavApartments, Icons.Material.Filled.Apartment, Audience: NavAudience.Admin, InBottomBar: false),
        new("admin/settings", UiStrings.NavSettings, Icons.Material.Filled.Payments, Audience: NavAudience.Admin, InBottomBar: false),
        new("admin/bank-accounts", UiStrings.NavBankAccountsAdmin, Icons.Material.Filled.AccountBalanceWallet, Audience: NavAudience.Admin, InBottomBar: false),
    ];

    public static IEnumerable<NavigationItem> For(ClaimsPrincipal user)
    {
        var isAdmin = user.HasClaim(MantenioClaims.Role, nameof(AccountRole.Admin));
        var isGuard = user.HasClaim(MantenioClaims.Role, nameof(AccountRole.Guard));
        return All.Where(item => item.Audience switch
        {
            NavAudience.ApartmentHolders => !isGuard,
            NavAudience.Admin => isAdmin,
            NavAudience.Guard => isGuard,
            _ => true,
        });
    }

    /// <summary>Admin pages listed on the hub page.</summary>
    public static IEnumerable<NavigationItem> AdminPages => All.Where(i => i.Audience == NavAudience.Admin && !i.InBottomBar);
}
