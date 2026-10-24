using System.ComponentModel.DataAnnotations;

namespace Mantenio.Shared.BankAccounts;

public sealed record BankAccountItem(
    Guid Id,
    string BankName,
    string HolderName,
    string AccountNumber,
    string Cci,
    bool IsActive,
    int SortOrder,
    DateTimeOffset UpdatedAt);

/// <summary>Bank, holder, account number and CCI only: never passwords or access keys.</summary>
public sealed record SaveBankAccountRequest(
    [property: Required, MaxLength(80)] string BankName,
    [property: Required, MaxLength(150)] string HolderName,
    [property: Required, MaxLength(40)] string AccountNumber,
    [property: Required, MaxLength(40)] string Cci,
    int SortOrder,
    [property: Required, MinLength(3), MaxLength(500)] string Reason);

public sealed record DeactivateBankAccountRequest([property: Required, MinLength(3), MaxLength(500)] string Reason);
