using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Mantenio.Shared.Ledger;

[JsonConverter(typeof(JsonStringEnumConverter<ProofStatus>))]
public enum ProofStatus
{
    Pending,
    Approved,
    Rejected,
    Voided,
}

[JsonConverter(typeof(JsonStringEnumConverter<DebtStatus>))]
public enum DebtStatus
{
    Pending,
    PartiallyPaid,
    Paid,
}

/// <param name="PayerName">Who the receipt is made out to when an admin registered the proof; null when the resident uploaded it.</param>
public sealed record PaymentProofItem(
    Guid Id,
    Guid ApartmentId,
    string ApartmentCode,
    string SubmittedByName,
    decimal DeclaredAmount,
    decimal? ApprovedAmount,
    DateOnly OperationDate,
    string BankName,
    string OperationNumber,
    ProofStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string? ReviewedByName,
    string? RejectionReason,
    string? VoidReason,
    string? ReceiptNumber,
    int RowVersion,
    string? PayerName = null)
{
    /// <summary>An admin registered it for the resident (see PayerName); SubmittedByName is that admin.</summary>
    public bool RegisteredByAdmin => PayerName is not null;
}

/// <summary>Short-lived link (about 5 minutes) to a private file.</summary>
public sealed record FileLink(Uri Url);

public sealed record ApproveProofRequest([property: Range(0.01, 99999)] decimal ApprovedAmount, int RowVersion);

public sealed record RejectProofRequest([property: Required, MinLength(3), MaxLength(500)] string Reason, int RowVersion);

public sealed record VoidProofRequest([property: Required, MinLength(3), MaxLength(500)] string Reason);

public sealed record ChargeItem(
    Guid Id,
    int Period,
    string Type,
    decimal Amount,
    decimal Paid,
    DebtStatus Status,
    DateOnly DueDate,
    bool Overdue);

public sealed record AccountStatement(
    Guid ApartmentId,
    string ApartmentCode,
    bool IsFrozen,
    IReadOnlyList<ChargeItem> Charges,
    IReadOnlyList<PaymentProofItem> Payments,
    decimal Credit,
    decimal OverdueDebt,
    decimal TotalOutstanding);

public sealed record TimelineEntry(DateTimeOffset At, string Action, string Source, string? ActorName, string? Details, string? Reason);

public sealed record GenerateChargesRequest(int Period);

public sealed record GenerateChargesResult(int Period, int Created, int SkippedFrozen, int AlreadyExisting);

public sealed record ImportOpeningDebtRequest([property: Required, MaxLength(200_000)] string Csv);

public sealed record ImportOpeningDebtResult(int Created, IReadOnlyList<string> Errors);
