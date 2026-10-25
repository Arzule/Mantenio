using Mantenio.Shared.Ledger;
using Mantenio.UI.Resources;
using MudBlazor;

namespace Mantenio.UI.Components;

/// <summary>Spanish labels and chip colors for ledger states (keys "ProofStatus_*", "DebtStatus_*", "ChargeType_*").</summary>
public static class LedgerText
{
    public static string Proof(ProofStatus status) => Text("ProofStatus_" + status);

    public static string Debt(DebtStatus status) => Text("DebtStatus_" + status);

    public static string ChargeType(string type) => Text("ChargeType_" + type);

    public static string TimelineAction(string action) => Text("Timeline_" + action, action);

    public static string AuditAction(string action) => Text("AuditAction_" + action, action);

    public static string AuditSource(string source) => Text("Source_" + source, source);

    public static string AuditEntity(string entityType) => Text("Entity_" + entityType, entityType);

    public static Color ProofColor(ProofStatus status) => status switch
    {
        ProofStatus.Approved => Color.Success,
        ProofStatus.Rejected => Color.Error,
        ProofStatus.Voided => Color.Dark,
        _ => Color.Warning,
    };

    public static Color DebtColor(ChargeItem charge) => charge switch
    {
        { Status: DebtStatus.Paid } => Color.Success,
        { Overdue: true } => Color.Error,
        { Status: DebtStatus.PartiallyPaid } => Color.Warning,
        _ => Color.Default,
    };

    /// <summary>"line:problem" from the opening debt import → Spanish sentence.</summary>
    public static string OpeningError(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var parts = error.Split(':', 2);
        return parts.Length == 2 ? Formats.Format(Text("OpeningError_" + parts[1], error), parts[0]) : error;
    }

    private static string Text(string key, string? fallback = null) =>
        UiStrings.ResourceManager.GetString(key, UiStrings.Culture) ?? fallback ?? key;
}
