using System.Text.Json.Serialization;

namespace Mantenio.Shared.Reports;

/// <summary>Overdue debt of every apartment on <see cref="Date"/>, grouped by tower (only apartments that owe).</summary>
public sealed record DelinquencyReport(DateOnly Date, IReadOnlyList<DelinquencyTower> Towers, decimal Total, int DelinquentApartments);

public sealed record DelinquencyTower(string Name, IReadOnlyList<DelinquencyRow> Rows, decimal Total);

/// <param name="Periods">yyyymm of each overdue month (opening debt and late fees count under their month).</param>
public sealed record DelinquencyRow(Guid ApartmentId, string Code, string Number, bool IsFrozen, IReadOnlyList<int> Periods, decimal Amount);

/// <summary>Admin home: what needs attention and how the month is going.</summary>
public sealed record AdminDashboard(
    int PendingProofs,
    decimal OverdueDebt,
    int DelinquentApartments,
    IReadOnlyList<TowerDebt> Towers,
    int CurrentPeriod,
    decimal CollectedThisMonth,
    int ReceiptsThisMonth);

public sealed record TowerDebt(string Tower, int DelinquentApartments, int Apartments, decimal Amount);

[JsonConverter(typeof(JsonStringEnumConverter<AuditSourceFilter>))]
public enum AuditSourceFilter
{
    Resident,
    Admin,
    System,

    /// <summary>Residents and admins together: everything people did, without the scheduled jobs.</summary>
    People,
}

public sealed record AuditItem(
    Guid Id,
    DateTimeOffset CreatedAt,
    string EntityType,
    string Action,
    string Source,
    string? ActorName,
    string? ApartmentCode,
    string? Reason,
    string? OldValues,
    string? NewValues);

public sealed record AuditActor(Guid Id, string Name);
