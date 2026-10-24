using System.Text.Json;
using Mantenio.Shared.Notifications;
using Mantenio.UI.Resources;

namespace Mantenio.UI.Components;

/// <summary>The Spanish title and body of a notice, built from its kind and data (the API stores no text).</summary>
public static class NoticeText
{
    public static string Title(NotificationItem notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return notice.Kind == NotificationKind.BankAccountsChanged ? UiStrings.NoticeBankTitle : UiStrings.NoticeFeeTitle;
    }

    public static string Body(NotificationItem notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var data = notice.Data;
        var reason = Formats.Format(UiStrings.NoticeReason, Text(data, "reason"));

        if (notice.Kind == NotificationKind.BankAccountsChanged)
        {
            var template = Text(data, "change") switch
            {
                BankAccountChanges.Added => UiStrings.NoticeBank_added,
                BankAccountChanges.Removed => UiStrings.NoticeBank_removed,
                _ => UiStrings.NoticeBank_updated,
            };
            return $"{Formats.Format(template, Text(data, "bankName"))} {reason} {UiStrings.NoticeBankCheck}";
        }

        var month = Formats.Month(data.GetProperty("effectivePeriod").GetInt32());
        var apartment = Text(data, "apartmentCode");
        var newAmount = Amount(data, "newAmount");
        var text = (apartment, newAmount) switch
        {
            ("", { } amount) => Formats.Format(UiStrings.NoticeFeeGlobal, Formats.Money(amount), month),
            (_, { } amount) => Formats.Format(UiStrings.NoticeFeeApartment, apartment, Formats.Money(amount), month),
            _ => Formats.Format(UiStrings.NoticeFeeApartmentRemoved, apartment, month),
        };
        return $"{text} {reason}";
    }

    private static string Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static decimal? Amount(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;
}
