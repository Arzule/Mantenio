namespace Mantenio.Domain.Banking;

/// <summary>
/// A condo bank account residents transfer to. Only bank, holder, account number and CCI (Peruvian interbank code,
/// 20 digits) are stored: never passwords or keys. Accounts are deactivated, not deleted, to keep the history.
/// </summary>
public sealed class BankAccount
{
    public const int CciLength = 20;
    public const int MaxAccountNumberLength = 30;

    private BankAccount() { } // EF Core

    public BankAccount(string bankName, string holderName, string accountNumber, string cci, int sortOrder, Guid adminId, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        IsActive = true;
        Update(bankName, holderName, accountNumber, cci, sortOrder, adminId, now);
    }

    public Guid Id { get; private set; }
    public string BankName { get; private set; } = "";
    public string HolderName { get; private set; } = "";
    public string AccountNumber { get; private set; } = "";
    public string Cci { get; private set; } = "";
    public bool IsActive { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedById { get; private set; }

    public void Update(string bankName, string holderName, string accountNumber, string cci, int sortOrder, Guid adminId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankName);
        ArgumentException.ThrowIfNullOrWhiteSpace(holderName);
        var number = NormalizeAccountNumber(accountNumber);
        var normalizedCci = NormalizeCci(cci);
        if (!IsValidAccountNumber(number))
        {
            throw new ArgumentException("Invalid account number.", nameof(accountNumber));
        }

        if (!IsValidCci(normalizedCci))
        {
            throw new ArgumentException("The CCI must have 20 digits.", nameof(cci));
        }

        BankName = bankName.Trim();
        HolderName = holderName.Trim();
        AccountNumber = number;
        Cci = normalizedCci;
        SortOrder = sortOrder;
        UpdatedById = adminId;
        UpdatedAt = now;
    }

    public void Deactivate(Guid adminId, DateTimeOffset now)
    {
        IsActive = false;
        UpdatedById = adminId;
        UpdatedAt = now;
    }

    /// <summary>Digits and dashes only (banks print them like "191-12345678-0-12"), spaces removed.</summary>
    public static string NormalizeAccountNumber(string? value) =>
        new((value ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    public static string NormalizeCci(string? value) => new((value ?? "").Where(char.IsAsciiDigit).ToArray());

    public static bool IsValidAccountNumber(string value) =>
        value.Length is > 0 and <= MaxAccountNumberLength
        && value.All(c => char.IsAsciiDigit(c) || c == '-')
        && value.Any(char.IsAsciiDigit);

    public static bool IsValidCci(string value) => value.Length == CciLength && value.All(char.IsAsciiDigit);
}
