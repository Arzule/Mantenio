using System.ComponentModel.DataAnnotations;

namespace Mantenio.Shared.Settings;

/// <summary>A billing month as yyyymm (e.g. 202611).</summary>
public static class PeriodValues
{
    public static int Year(int period) => period / 100;

    public static int Month(int period) => period % 100;

    public static bool IsValid(int period) => Year(period) is >= 2000 and <= 9999 && Month(period) is >= 1 and <= 12;
}

public sealed record CondoSettingsResponse(
    decimal CurrentGlobalFee,
    int CurrentPeriod,
    int DueDay,
    int DueMonthOffset,
    decimal LateFeePercent,
    int TrackingStartPeriod,
    int MaxAdmins,
    IReadOnlyList<FeeChangeItem> ScheduledGlobalChanges,
    IReadOnlyList<string>? BoardEmails = null);

/// <summary>Founder: the board members who get the monthly summary (folio, period report, audit fingerprint).</summary>
public sealed record ChangeBoardEmailsRequest(
    [property: MaxLength(10)] IReadOnlyList<string> Emails,
    [property: Required, MinLength(3), MaxLength(500)] string Reason);

public sealed record FeeChangeItem(
    Guid Id,
    string Scope,
    Guid? ApartmentId,
    string? ApartmentCode,
    decimal? OldAmount,
    decimal? NewAmount,
    int EffectivePeriod,
    string Reason,
    string ChangedByName,
    DateTimeOffset CreatedAt);

/// <summary>What a signed-in user pays: the fee of their apartment now and, if scheduled, from which month it changes.</summary>
public sealed record MyFeeResponse(
    string? ApartmentCode,
    decimal? CurrentFee,
    int CurrentPeriod,
    DateOnly? CurrentDueDate,
    decimal? NextFee,
    int? NextFeePeriod);

public sealed record ChangeGlobalFeeRequest(
    [property: Range(0, 99999)] decimal NewAmount,
    int EffectivePeriod,
    [property: Required, MinLength(3), MaxLength(500)] string Reason);

/// <summary><see cref="NewAmount"/> null removes the apartment's special fee from that month on.</summary>
public sealed record ChangeApartmentFeeRequest(
    [property: Range(0, 99999)] decimal? NewAmount,
    int EffectivePeriod,
    [property: Required, MinLength(3), MaxLength(500)] string Reason);

public sealed record ChangeDueRuleRequest(
    [property: Range(1, 28)] int DueDay,
    [property: Range(0, 1)] int DueMonthOffset,
    [property: Required, MinLength(3), MaxLength(500)] string Reason);

public sealed record ChangeLateFeeRequest(
    [property: Range(0, 100)] decimal Percent,
    [property: Required, MinLength(3), MaxLength(500)] string Reason);
