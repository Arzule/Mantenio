using System.Globalization;
using Mantenio.Api.Resources;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Condos;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Mantenio.Api.Features.Ledger;

/// <summary>Everything printed on a receipt. Dates are already in the condo time zone.</summary>
public sealed record ReceiptData(
    string Number,
    decimal Amount,
    DateOnly IssuedOn,
    DateTimeOffset IssuedAtLocal,
    string PayerName,
    string Tower,
    string ApartmentNumber,
    IReadOnlyList<int> Periods,
    decimal Credit,
    DateOnly OperationDate,
    string OperationNumber,
    string BankName,
    string ApprovedByName,
    bool Voided);

/// <summary>
/// Receipt PDF with the same layout as the paper receipt of the condo (see docs, "Formato del recibo PDF"):
/// logo and name, "RECIBO DE PAGO" with the number, amount, payer, apartment, amount in words, concept,
/// months paid, bank operation, CANCELADO (or ANULADO) stamp and "Aprobado por" instead of a signature.
/// Labels live in Resources/ReceiptTexts.resx.
/// </summary>
public static class ReceiptPdf
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);

    static ReceiptPdf()
    {
        // Community license: free for organisations under US$1M revenue (the condo board qualifies).
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Render(ReceiptData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var profile = CondoProfile.Current;
        var primary = Color.FromHex(profile.Theme.Primary);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A5.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(t => t.FontSize(10));

            page.Content().Column(column =>
            {
                column.Spacing(8);
                column.Item().Row(row =>
                {
                    row.ConstantItem(56).Height(56).Svg(CondoProfile.LogoSvg);
                    row.RelativeItem().PaddingLeft(10).Column(header =>
                    {
                        header.Item().Text(profile.OrganizationAcronym).Bold().FontSize(14).FontColor(primary);
                        header.Item().Text(profile.DisplayName).SemiBold();
                    });
                    row.RelativeItem().AlignRight().AlignMiddle().Text(profile.Address);
                });

                column.Item().Row(row =>
                {
                    row.RelativeItem().AlignMiddle().Text(ReceiptTexts.Title).Bold().FontSize(16).FontColor(primary);
                    row.ConstantItem(170).Border(1).BorderColor(primary).Padding(6).AlignCenter()
                        .Text($"{data.Number.Split('-')[0]}- Nº {data.Number.Split('-')[^1]}").Bold();
                });

                column.Item().Row(row =>
                {
                    row.RelativeItem().Text(text =>
                    {
                        text.Span(ReceiptTexts.Date).SemiBold();
                        text.Span(data.IssuedOn.ToString("dd/MM/yyyy", Culture));
                    });
                    row.ConstantItem(170).Border(1).BorderColor(primary).Padding(6).AlignCenter()
                        .Text(data.Amount.ToString("C2", Culture)).Bold().FontSize(13);
                });

                column.Item().Text(text =>
                {
                    text.Span(ReceiptTexts.ReceivedFrom).SemiBold();
                    text.Span(data.PayerName);
                });
                column.Item().Text(text =>
                {
                    text.Span($"{profile.TowerLabel}: ").SemiBold();
                    text.Span(data.Tower);
                    text.Span("     " + ReceiptTexts.Apartment).SemiBold();
                    text.Span(data.ApartmentNumber);
                });
                column.Item().Text(text =>
                {
                    text.Span(ReceiptTexts.AmountOf).SemiBold();
                    text.Span(SpanishAmountInWords.Soles(data.Amount));
                });
                column.Item().Text(text =>
                {
                    text.Span(ReceiptTexts.Concept).SemiBold();
                    text.Span(profile.Receipt.Concept);
                });
                column.Item().Text(text =>
                {
                    text.Span(ReceiptTexts.Months).SemiBold();
                    text.Span(data.Periods.Count == 0 ? "—" : string.Join(", ", data.Periods.Select(MonthName)));
                });
                if (data.Credit > 0)
                {
                    column.Item().Text(text =>
                    {
                        text.Span(ReceiptTexts.Credit).SemiBold();
                        text.Span(data.Credit.ToString("C2", Culture));
                    });
                }

                column.Item().Text(text =>
                {
                    text.Span(ReceiptTexts.BankDate).SemiBold();
                    text.Span(data.OperationDate.ToString("dd/MM/yyyy", Culture));
                    text.Span("     " + ReceiptTexts.OperationNumber).SemiBold();
                    text.Span($"{data.OperationNumber} ({data.BankName})");
                });

                column.Item().PaddingTop(10).Row(row =>
                {
                    row.RelativeItem().AlignMiddle().Text(data.Voided ? ReceiptTexts.Voided : ReceiptTexts.Paid)
                        .Bold().FontSize(22).FontColor(data.Voided ? Colors.Red.Darken2 : Colors.Green.Darken2);
                    row.ConstantItem(220).Column(signature =>
                    {
                        signature.Item().AlignCenter().Text(Format(ReceiptTexts.ApprovedBy, data.ApprovedByName));
                        signature.Item().PaddingTop(2).LineHorizontal(1);
                        signature.Item().AlignCenter().Text(profile.Receipt.SignerTitle).SemiBold();
                    });
                });
            });

            page.Footer().AlignCenter().Text(Format(ReceiptTexts.Footer, data.IssuedAtLocal.ToString("dd/MM/yyyy HH:mm", Culture)))
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        })).GeneratePdf();
    }

    private static string Format(string template, params object[] args) => string.Format(Culture, template, args);

    private static string MonthName(int period) =>
        new DateTime(period / 100, period % 100, 1).ToString("MMMM yyyy", Culture).ToUpper(Culture);
}
