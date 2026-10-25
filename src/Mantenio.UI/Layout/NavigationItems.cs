using System.Security.Claims;
using Mantenio.Shared.Auth;
using Mantenio.UI.Resources;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;

namespace Mantenio.UI.Layout;

/// <param name="InBottomBar">Shown in the phone bottom bar (at most 4 items).</param>
/// <param name="InDrawer">Shown in the desktop side drawer.</param>
public sealed record NavigationItem(
    string Href,
    string Title,
    string Icon,
    NavLinkMatch Match = NavLinkMatch.Prefix,
    bool AdminOnly = false,
    bool InBottomBar = true,
    bool InDrawer = true);

/// <summary>Single source for the side drawer (desktop) and the bottom bar (mobile). Phases add items here.</summary>
public static class NavigationItems
{
    public static IReadOnlyList<NavigationItem> All { get; } =
    [
        new("", UiStrings.NavHome, Icons.Material.Filled.Home, NavLinkMatch.All),
        new("payments", UiStrings.NavPayments, Icons.Material.Filled.ReceiptLong),

        // Also linked from the payments page ("Ver cuentas para pagar"), so it leaves room in the bottom bar.
        new("bank-accounts", UiStrings.NavBankAccounts, Icons.Material.Filled.AccountBalance, InBottomBar: false),
        new("notifications", UiStrings.NavNotifications, Icons.Material.Filled.Notifications),
        new("help", UiStrings.NavHelp, Icons.Material.Filled.HelpOutline, InBottomBar: false),

        // Phones get one "Administración" entry (a hub page with the dashboard); the desktop drawer also lists every admin page.
        new("admin", UiStrings.NavAdmin, Icons.Material.Filled.AdminPanelSettings, NavLinkMatch.All, AdminOnly: true),
        new("admin/payments", UiStrings.NavPaymentReview, Icons.Material.Filled.FactCheck, AdminOnly: true, InBottomBar: false),
        new("admin/reports", UiStrings.NavReports, Icons.Material.Filled.Assessment, AdminOnly: true, InBottomBar: false),
        new("admin/audit", UiStrings.NavAudit, Icons.Material.Filled.History, AdminOnly: true, InBottomBar: false),
        new("admin/charges", UiStrings.NavCharges, Icons.Material.Filled.RequestQuote, AdminOnly: true, InBottomBar: false),
        new("admin/users", UiStrings.NavUsers, Icons.Material.Filled.People, AdminOnly: true, InBottomBar: false),
        new("admin/invitations", UiStrings.NavInvitations, Icons.Material.Filled.PersonAdd, AdminOnly: true, InBottomBar: false),
        new("admin/apartments", UiStrings.NavApartments, Icons.Material.Filled.Apartment, AdminOnly: true, InBottomBar: false),
        new("admin/settings", UiStrings.NavSettings, Icons.Material.Filled.Payments, AdminOnly: true, InBottomBar: false),
        new("admin/bank-accounts", UiStrings.NavBankAccountsAdmin, Icons.Material.Filled.AccountBalanceWallet, AdminOnly: true, InBottomBar: false),
    ];

    public static IEnumerable<NavigationItem> For(ClaimsPrincipal user)
    {
        var isAdmin = user.HasClaim(MantenioClaims.Role, nameof(AccountRole.Admin));
        return All.Where(item => !item.AdminOnly || isAdmin);
    }

    /// <summary>Admin pages listed on the hub page.</summary>
    public static IEnumerable<NavigationItem> AdminPages => All.Where(i => i.AdminOnly && !i.InBottomBar);
}
