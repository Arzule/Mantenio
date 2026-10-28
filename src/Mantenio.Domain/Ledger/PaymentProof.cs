namespace Mantenio.Domain.Ledger;

public enum PaymentProofStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Voided = 3,

    /// <summary>A payment from before the app (paper ledger), recorded for the history; not applied to the debt yet.</summary>
    Historical = 4,
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
        string imageObjectKey, DateTimeOffset now, string? payerName = null)
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
        PayerName = string.IsNullOrWhiteSpace(payerName) ? null : payerName.Trim();
    }

    /// <summary>
    /// A payment made before the app was used, loaded by an admin from the paper records. Bank, operation number,
    /// paper receipt number and photo are optional (the photo can be attached later); it does not touch the debt.
    /// </summary>
    public static PaymentProof Historical(
        Guid apartmentId, Guid adminId, decimal amount, DateOnly operationDate, string? payerName, string? bankName,
        string? operationNumber, string? paperReceiptNumber, string? imageObjectKey, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        var proof = new PaymentProof
        {
            Id = Guid.CreateVersion7(),
            ApartmentId = apartmentId,
            SubmittedById = adminId,
            Status = PaymentProofStatus.Historical,
            SubmittedAt = now,
        };
        proof.SetHistoricalDetails(amount, operationDate, payerName, bankName, operationNumber, paperReceiptNumber);
        proof.ImageObjectKey = imageObjectKey ?? "";
        return proof;
    }

    /// <summary>
    /// An admin (e.g. the guard on duty) registered it for a resident who brought the voucher in person.
    /// <see cref="PayerName"/> is then who the receipt is made out to; otherwise it is the user who uploaded it.
    /// </summary>
    public static PaymentProof RegisteredByAdmin(
        Guid apartmentId, Guid adminId, string payerName, decimal amount, DateOnly operationDate, string bankName, string operationNumber,
        string imageObjectKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payerName);
        return new PaymentProof(apartmentId, adminId, amount, operationDate, bankName, operationNumber, imageObjectKey, now, payerName);
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
    /// <summary>Where the photo is stored; empty when there is none (historical payment without photo, or removed by retention).</summary>
    public string ImageObjectKey { get; private set; } = "";

    public bool HasImage => ImageObjectKey.Length > 0;

    /// <summary>Number of the paper receipt of a historical payment, when it is known.</summary>
    public string? PaperReceiptNumber { get; private set; }

    /// <summary>Set when an admin registered the proof for a resident (see <see cref="RegisteredByAdmin"/>).</summary>
    public string? PayerName { get; private set; }

    public bool IsRegisteredByAdmin => PayerName is not null;
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

    /// <summary>Corrects the data of a historical payment (e.g. once its photo is attached and read).</summary>
    public void SetHistoricalDetails(
        decimal amount, DateOnly operationDate, string? payerName, string? bankName, string? operationNumber, string? paperReceiptNumber)
    {
        RequireStatus(PaymentProofStatus.Historical);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        DeclaredAmount = decimal.Round(amount, 2);
        OperationDate = operationDate;
        PayerName = Clean(payerName);
        BankName = Clean(bankName) ?? "";
        OperationNumber = NormalizeOperationNumber(operationNumber ?? "");
        PaperReceiptNumber = Clean(paperReceiptNumber);
    }

    /// <summary>Points to a new photo (historical payments only); returns the previous key so its file can be deleted.</summary>
    public string ReplaceImage(string imageObjectKey)
    {
        RequireStatus(PaymentProofStatus.Historical);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageObjectKey);
        var previous = ImageObjectKey;
        ImageObjectKey = imageObjectKey;
        return previous;
    }

    /// <summary>Forgets the photo once its file was deleted by the retention policy (the ledger row stays).</summary>
    public void RemoveImage() => ImageObjectKey = "";

    /// <summary>Digits and letters only, upper case: "00 1234-56" and "00123456" are the same operation.</summary>
    public static string NormalizeOperationNumber(string value) =>
        new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
