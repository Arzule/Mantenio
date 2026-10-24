using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mantenio.Shared.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter<NotificationKind>))]
public enum NotificationKind
{
    BankAccountsChanged,
    FeeChanged,
}

/// <summary>A notice; the UI writes the Spanish text from <see cref="Kind"/> and <see cref="Data"/>.</summary>
public sealed record NotificationItem(
    Guid Id,
    NotificationKind Kind,
    JsonElement Data,
    bool RequiresAcknowledgement,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? AcknowledgedAt);

public sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, int UnreadCount);

/// <summary>Data of <see cref="NotificationKind.FeeChanged"/>. Apartment null = global fee.</summary>
public sealed record FeeChangedData(string? ApartmentCode, decimal? OldAmount, decimal? NewAmount, int EffectivePeriod, string Reason);

/// <summary>Data of <see cref="NotificationKind.BankAccountsChanged"/>.</summary>
public sealed record BankAccountsChangedData(string Change, string BankName, string Reason);

public static class BankAccountChanges
{
    public const string Added = "added";
    public const string Updated = "updated";
    public const string Removed = "removed";
}
