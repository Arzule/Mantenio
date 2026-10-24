namespace Mantenio.Domain.Ledger;

public enum PaymentProofStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Voided = 3,
}

/// <summary>
/// The screenshot of a bank transfer a resident uploads for their apartment, and its review. Never deleted:
/// rejected and voided proofs stay with their reason.
/// </summary>
public sealed class PaymentProof
{
    private PaymentProof() { } // EF Core

    public PaymentProof(
        Guid apartmentId, Guid submittedById, decimal declaredAmount, DateOnly operationDate, string bankName, string operationNumber,
        string imageObjectKey, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(declaredAmount);
        ArgumentException.ThrowIfNullOrWhiteSpace(bankName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageObjectKey);
        Id = Guid.CreateVersion7();
        ApartmentId = apartmentId;
        SubmittedById = submittedById;
        DeclaredAmount = decimal.Round(declaredAmount, 2);
        OperationDate = operationDate;
        BankName = bankName.Trim();
        OperationNumber = NormalizeOperationNumber(operationNumber);
        ImageObjectKey = imageObjectKey;
        Status = PaymentProofStatus.Pending;
        SubmittedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ApartmentId { get; private set; }
    public Guid SubmittedById { get; private set; }
    public decimal DeclaredAmount { get; private set; }

    /// <summary>The amount the admin confirmed by looking at the screenshot.</summary>
    public decimal? ApprovedAmount { get; private set; }

    public DateOnly OperationDate { get; private set; }
    public string BankName { get; private set; } = "";
    public string OperationNumber { get; private set; } = "";
    public string ImageObjectKey { get; private set; } = "";
    public PaymentProofStatus Status { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public Guid? ReviewedById { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public string? RejectionReason { get; private set; }
    public Guid? VoidedById { get; private set; }
    public DateTimeOffset? VoidedAt { get; private set; }
    public string? VoidReason { get; private set; }

    public void Approve(decimal approvedAmount, Guid adminId, DateTimeOffset now)
    {
        RequireStatus(PaymentProofStatus.Pending);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(approvedAmount);
        ApprovedAmount = decimal.Round(approvedAmount, 2);
        Status = PaymentProofStatus.Approved;
        ReviewedById = adminId;
        ReviewedAt = now;
    }

    public void Reject(string reason, Guid adminId, DateTimeOffset now)
    {
        RequireStatus(PaymentProofStatus.Pending);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        RejectionReason = reason.Trim();
        Status = PaymentProofStatus.Rejected;
        ReviewedById = adminId;
        ReviewedAt = now;
    }

    /// <summary>Undoes an approved proof (the money is returned outside the system).</summary>
    public void Void(string reason, Guid adminId, DateTimeOffset now)
    {
        RequireStatus(PaymentProofStatus.Approved);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        VoidReason = reason.Trim();
        Status = PaymentProofStatus.Voided;
        VoidedById = adminId;
        VoidedAt = now;
    }

    /// <summary>Digits and letters only, upper case: "00 1234-56" and "00123456" are the same operation.</summary>
    public static string NormalizeOperationNumber(string value) =>
        new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private void RequireStatus(PaymentProofStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"The payment proof is {Status}, expected {expected}.");
        }
    }
}

/// <summary>How much of a payment went to a charge. Reversed (not deleted) when its payment proof is voided.</summary>
public sealed class PaymentAllocation
{
    private PaymentAllocation() { } // EF Core

    public PaymentAllocation(Guid paymentProofId, Guid chargeId, decimal amount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        Id = Guid.CreateVersion7();
        PaymentProofId = paymentProofId;
        ChargeId = chargeId;
        Amount = amount;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid PaymentProofId { get; private set; }
    public Guid ChargeId { get; private set; }
    public decimal Amount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReversedAt { get; private set; }

    public bool IsActive => ReversedAt is null;

    public void Reverse(DateTimeOffset now) => ReversedAt ??= now;
}
