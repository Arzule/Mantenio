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
        return notice.Kind switch
        {
            NotificationKind.BankAccountsChanged => UiStrings.NoticeBankTitle,
            NotificationKind.PaymentApproved => UiStrings.NoticePaymentApprovedTitle,
            NotificationKind.PaymentRejected => UiStrings.NoticePaymentRejectedTitle,
            NotificationKind.PaymentVoided => UiStrings.NoticePaymentVoidedTitle,
            _ => UiStrings.NoticeFeeTitle,
        };
    }

    public static string Body(NotificationItem notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var data = notice.Data;
        var reason = Formats.Format(UiStrings.NoticeReason, Text(data, "reason").TrimEnd('.', ' '));

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

        if (notice.Kind is NotificationKind.PaymentApproved or NotificationKind.PaymentRejected or NotificationKind.PaymentVoided)
        {
            var amount = Formats.Money(Amount(data, "amount") ?? 0);
            var receipt = Text(data, "receiptNumber");
            return notice.Kind switch
            {
                NotificationKind.PaymentApproved => Formats.Format(UiStrings.NoticePaymentApproved, amount, receipt),
                NotificationKind.PaymentRejected => $"{Formats.Format(UiStrings.NoticePaymentRejected, amount)} {reason}",
                _ => $"{Formats.Format(UiStrings.NoticePaymentVoided, amount, receipt)} {reason}",
            };
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
