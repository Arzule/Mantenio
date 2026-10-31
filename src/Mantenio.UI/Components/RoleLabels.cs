using Mantenio.Shared.Auth;
using Mantenio.UI.Resources;

namespace Mantenio.UI.Components;

/// <summary>Spanish name of each role ("Superadmin" for the founder).</summary>
public static class RoleLabels
{
    public static string For(AccountRole role, bool isFounder = false) =>
        isFounder ? UiStrings.RoleFounder
        : role switch
        {
            AccountRole.Admin => UiStrings.RoleAdmin,
            AccountRole.Guard => UiStrings.RoleGuard,
            _ => UiStrings.RoleResident,
        };
}
