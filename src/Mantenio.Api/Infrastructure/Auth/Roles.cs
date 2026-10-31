using Mantenio.Domain.Audit;
using Mantenio.Domain.Identity;
using Mantenio.Shared.Auth;

namespace Mantenio.Api.Infrastructure.Auth;

/// <summary>Maps the stored role to the one in the contracts and the audit log.</summary>
internal static class Roles
{
    public static AccountRole ToAccountRole(UserRole role) => role switch
    {
        UserRole.Admin => AccountRole.Admin,
        UserRole.Guard => AccountRole.Guard,
        _ => AccountRole.Resident,
    };

    public static UserRole ToUserRole(AccountRole role) => role switch
    {
        AccountRole.Admin => UserRole.Admin,
        AccountRole.Guard => UserRole.Guard,
        _ => UserRole.Resident,
    };

    public static AuditSource ToAuditSource(UserRole role) => role switch
    {
        UserRole.Admin => AuditSource.Admin,
        UserRole.Guard => AuditSource.Guard,
        _ => AuditSource.Resident,
    };
}
