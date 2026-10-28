using System.Globalization;
using ClosedXML.Excel;
using Mantenio.Api.Resources;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Reports;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Mantenio.Api.Features.Reports;

/// <summary>One receipt line of the monthly folio. Dates are already in the condo time zone.</summary>
public sealed record FolioLine(
    string Number, DateTimeOffset IssuedAtLocal, string ApartmentCode, string PayerName, IReadOnlyList<int> Periods,
    decimal Amount, string Operation, bool Voided);

/// <summary>
/// Report files generated on demand and never stored: the delinquency report (Excel with one sheet per tower, PDF with
/// one section per tower) and the monthly folio (PDF). Labels live in Resources/ReportTexts.resx.
/// </summary>
public static class ReportFiles
{
    public const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string PdfContentType = "application/pdf";

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);

    static ReportFiles()
    {
        // Community license: free for organisations under US$1M revenue (the condo board qualifies).
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static string DelinquencyFileName(DateOnly date, string extension) =>
        Format(ReportTexts.DelinquencyFileName, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + extension;

    public static string FolioFileName(int period) =>
        Format(ReportTexts.FolioFileName, (period / 100).ToString("0000", CultureInfo.InvariantCulture) + "-"
            + (period % 100).ToString("00", CultureInfo.InvariantCulture)) + ".pdf";

    public static byte[] DelinquencyExcel(DelinquencyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var workbook = new XLWorkbook();
        var title = Format(ReportTexts.DelinquencyTitle, Date(report.Date));

        var summary = workbook.Worksheets.Add(ReportTexts.Summary);
        summary.Cell(1, 1).Value = CondoProfile.Current.DisplayName;
        summary.Cell(2, 1).Value = title;
        summary.Range(1, 1, 2, 1).Style.Font.Bold = true;
        Header(summary, 4, CondoProfile.Current.TowerLabel, ReportTexts.DelinquentApartments, ReportTexts.Amount);
        var row = 5;
        foreach (var tower in report.Towers)
        {
            summary.Cell(row, 1).Value = tower.Name;
            summary.Cell(row, 2).Value = tower.Rows.Count;
            summary.Cell(row, 3).Value = tower.Total;
            row++;
        }

        summary.Cell(row, 1).Value = ReportTexts.Total;
        summary.Cell(row, 2).Value = report.DelinquentApartments;
        summary.Cell(row, 3).Value = report.Total;
        summary.Row(row).Style.Font.Bold = true;
        summary.Column(3).Style.NumberFormat.Format = "#,##0.00";
        summary.Columns().AdjustToContents();

        foreach (var tower in report.Towers)
        {
            // Sheet names: max 31 chars, no []:*?/\
            var name = new string(tower.Name.Where(c => "[]:*?/\\".IndexOf(c, StringComparison.Ordinal) < 0).ToArray());
            var sheet = workbook.Worksheets.Add(string.IsNullOrWhiteSpace(name) ? "-" : name[..Math.Min(name.Length, 31)]);
            sheet.Cell(1, 1).Value = $"{CondoProfile.Current.TowerLabel} {tower.Name}";
            sheet.Cell(2, 1).Value = title;
            sheet.Range(1, 1, 2, 1).Style.Font.Bold = true;
            Header(sheet, 4, ReportTexts.Apartment, ReportTexts.OverdueMonths, ReportTexts.MonthCount, ReportTexts.Amount, ReportTexts.Status);
            var line = 5;
            foreach (var item in tower.Rows)
            {
                sheet.Cell(line, 1).Value = item.Code;
                sheet.Cell(line, 2).Value = string.Join(", ", item.Periods.Select(MonthName));
                sheet.Cell(line, 3).Value = item.Periods.Count;
                sheet.Cell(line, 4).Value = item.Amount;
                sheet.Cell(line, 5).Value = item.IsFrozen ? ReportTexts.Frozen : "";
                line++;
            }

            sheet.Cell(line, 1).Value = ReportTexts.Total;
            sheet.Cell(line, 4).Value = tower.Total;
            sheet.Row(line).Style.Font.Bold = true;
            sheet.Column(4).Style.NumberFormat.Format = "#,##0.00";
            sheet.Columns().AdjustToContents();
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static string PeriodFileName(int from, int to) =>
        Format(ReportTexts.PeriodFileName, IsoMonth(from), IsoMonth(to)) + ".xlsx";

    /// <summary>
    /// The audit report of a range of months: a summary (totals and debt per tower), every charge of the period with
    /// what was paid by the close, every payment of the period, the rejected and voided ones apart, and one sheet per tower.
    /// </summary>
    public static byte[] PeriodExcel(PeriodReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var workbook = new XLWorkbook();
        var title = Format(ReportTexts.PeriodTitle, MonthName(report.From), MonthName(report.To));
        var close = Format(ReportTexts.PeriodClose, Date(report.CloseDate));

        // Summary
        var summary = workbook.Worksheets.Add(ReportTexts.Summary);
        summary.Cell(1, 1).Value = CondoProfile.Current.DisplayName;
        summary.Cell(2, 1).Value = title;
        summary.Cell(3, 1).Value = close;
        summary.Range(1, 1, 2, 1).Style.Font.Bold = true;
        Header(summary, 5, ReportTexts.Concept, ReportTexts.Count, ReportTexts.Amount);
        var row = 6;
        void Line(string label, int count, decimal amount)
        {
            summary.Cell(row, 1).Value = label;
            summary.Cell(row, 2).Value = count;
            summary.Cell(row, 3).Value = amount;
            row++;
        }

        foreach (var type in new[] { ChargeType.Maintenance, ChargeType.Opening, ChargeType.LateFee })
        {
            var ofType = report.Charges.Where(c => c.Type == type).ToList();
            Line(Format(ReportTexts.ChargedOfType, ChargeTypeName(type)), ofType.Count, ofType.Sum(c => c.Amount));
        }

        Line(ReportTexts.PaidOnPeriodCharges, report.Charges.Count(c => c.PaidAtClose > 0), report.Charges.Sum(c => c.PaidAtClose));
        Line(ReportTexts.OutstandingOnPeriodCharges, report.Charges.Count(c => c.Outstanding > 0), report.Charges.Sum(c => c.Outstanding));
        foreach (var status in new[]
                 {
                     PaymentProofStatus.Approved, PaymentProofStatus.Historical, PaymentProofStatus.Pending, PaymentProofStatus.Rejected,
                     PaymentProofStatus.Voided,
                 })
        {
            var ofStatus = report.Payments.Where(p => p.Status == status).ToList();
            Line(Format(ReportTexts.PaymentsOfStatus, StatusName(status)), ofStatus.Count, ofStatus.Sum(p => p.Approved ?? p.Declared));
        }

        Line(ReportTexts.DelinquencyAtClose, report.DelinquencyAtClose.DelinquentApartments, report.DelinquencyAtClose.Total);
        summary.Column(3).Style.NumberFormat.Format = "#,##0.00";

        row++;
        Header(summary, row++, CondoProfile.Current.TowerLabel, ReportTexts.Charged, ReportTexts.Paid, ReportTexts.Outstanding);
        foreach (var tower in report.Charges.GroupBy(c => (c.Tower, c.TowerOrder)).OrderBy(g => g.Key.TowerOrder).ThenBy(g => g.Key.Tower, StringComparer.Ordinal))
        {
            summary.Cell(row, 1).Value = tower.Key.Tower;
            summary.Cell(row, 2).Value = tower.Sum(c => c.Amount);
            summary.Cell(row, 3).Value = tower.Sum(c => c.PaidAtClose);
            summary.Cell(row, 4).Value = tower.Sum(c => c.Outstanding);
            row++;
        }

        summary.Range(6, 2, row, 4).Style.NumberFormat.Format = "#,##0.00";
        summary.Range(6, 2, 6 + 11, 2).Style.NumberFormat.Format = "0";
        summary.Columns().AdjustToContents();

        // Charges
        var chargesSheet = workbook.Worksheets.Add(ReportTexts.ChargesSheet);
        Header(chargesSheet, 1, CondoProfile.Current.TowerLabel, ReportTexts.Apartment, ReportTexts.Month, ReportTexts.Type, ReportTexts.Amount,
            ReportTexts.Paid, ReportTexts.Outstanding, ReportTexts.DueDate);
        row = 2;
        foreach (var c in report.Charges)
        {
            chargesSheet.Cell(row, 1).Value = c.Tower;
            chargesSheet.Cell(row, 2).Value = c.Apartment;
            chargesSheet.Cell(row, 3).Value = MonthName(c.Period);
            chargesSheet.Cell(row, 4).Value = ChargeTypeName(c.Type);
            chargesSheet.Cell(row, 5).Value = c.Amount;
            chargesSheet.Cell(row, 6).Value = c.PaidAtClose;
            chargesSheet.Cell(row, 7).Value = c.Outstanding;
            chargesSheet.Cell(row, 8).Value = Date(c.DueDate);
            row++;
        }

        chargesSheet.Range(2, 5, Math.Max(row, 2), 7).Style.NumberFormat.Format = "#,##0.00";
        Finish(chargesSheet, row);

        // Payments (all) and the rejected/voided ones apart
        PaymentsSheet(workbook.Worksheets.Add(ReportTexts.PaymentsSheet), report.Payments);
        PaymentsSheet(workbook.Worksheets.Add(ReportTexts.RejectedVoidedSheet),
            report.Payments.Where(p => p.Status is PaymentProofStatus.Rejected or PaymentProofStatus.Voided).ToList());

        // One sheet per tower: totals per apartment
        foreach (var tower in report.Charges.GroupBy(c => (c.Tower, c.TowerOrder)).OrderBy(g => g.Key.TowerOrder).ThenBy(g => g.Key.Tower, StringComparer.Ordinal))
        {
            var name = new string(tower.Key.Tower.Where(c => "[]:*?/\\".IndexOf(c, StringComparison.Ordinal) < 0).ToArray());
            var sheet = workbook.Worksheets.Add(string.IsNullOrWhiteSpace(name) ? "-" : name[..Math.Min(name.Length, 31)]);
            sheet.Cell(1, 1).Value = $"{CondoProfile.Current.TowerLabel} {tower.Key.Tower}";
            sheet.Cell(2, 1).Value = title;
            sheet.Cell(3, 1).Value = close;
            sheet.Range(1, 1, 2, 1).Style.Font.Bold = true;
            Header(sheet, 5, ReportTexts.Apartment, ReportTexts.Charged, ReportTexts.Paid, ReportTexts.Outstanding, ReportTexts.UnpaidMonths);
            row = 6;
            foreach (var apartment in tower.GroupBy(c => (c.Apartment, c.Number)).OrderBy(g => g.Key.Number.Length).ThenBy(g => g.Key.Number, StringComparer.Ordinal))
            {
                sheet.Cell(row, 1).Value = apartment.Key.Apartment;
                sheet.Cell(row, 2).Value = apartment.Sum(c => c.Amount);
                sheet.Cell(row, 3).Value = apartment.Sum(c => c.PaidAtClose);
                sheet.Cell(row, 4).Value = apartment.Sum(c => c.Outstanding);
                sheet.Cell(row, 5).Value = string.Join(", ", apartment.Where(c => c.Outstanding > 0).Select(c => c.Period).Distinct().Order().Select(ShortMonth));
                row++;
            }

            sheet.Cell(row, 1).Value = ReportTexts.Total;
            sheet.Cell(row, 2).Value = tower.Sum(c => c.Amount);
            sheet.Cell(row, 3).Value = tower.Sum(c => c.PaidAtClose);
            sheet.Cell(row, 4).Value = tower.Sum(c => c.Outstanding);
            sheet.Row(row).Style.Font.Bold = true;
            sheet.Range(6, 2, row, 4).Style.NumberFormat.Format = "#,##0.00";
            sheet.Columns().AdjustToContents();
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>One row per payment: date, apartment, payer, amounts, bank operation, state, receipt, who registered and reviewed it, reason.</summary>
    private static void PaymentsSheet(IXLWorksheet sheet, IReadOnlyList<PeriodPaymentLine> payments)
    {
        Header(sheet, 1, ReportTexts.Date, CondoProfile.Current.TowerLabel, ReportTexts.Apartment, ReportTexts.ReceivedFrom, ReportTexts.Declared,
            ReportTexts.Approved, ReportTexts.Bank, ReportTexts.Operation, ReportTexts.Status, ReportTexts.ReceiptNumber, ReportTexts.RegisteredBy,
            ReportTexts.ReviewedBy, ReportTexts.ReviewedAt, ReportTexts.Reason);
        var row = 2;
        foreach (var p in payments)
        {
            sheet.Cell(row, 1).Value = Date(p.OperationDate);
            sheet.Cell(row, 2).Value = p.Tower;
            sheet.Cell(row, 3).Value = p.Apartment;
            sheet.Cell(row, 4).Value = p.Payer;
            sheet.Cell(row, 5).Value = p.Declared;
            if (p.Approved is { } approved)
            {
                sheet.Cell(row, 6).Value = approved;
            }

            sheet.Cell(row, 7).Value = p.Bank;
            sheet.Cell(row, 8).Value = p.Operation;
            sheet.Cell(row, 9).Value = StatusName(p.Status);
            sheet.Cell(row, 10).Value = p.Receipt ?? "";
            sheet.Cell(row, 11).Value = p.RegisteredBy;
            sheet.Cell(row, 12).Value = p.ReviewedBy ?? "";
            sheet.Cell(row, 13).Value = p.ReviewedAtLocal?.ToString("dd/MM/yyyy HH:mm", Culture) ?? "";
            sheet.Cell(row, 14).Value = p.Reason ?? "";
            row++;
        }

        sheet.Range(2, 5, Math.Max(row, 2), 6).Style.NumberFormat.Format = "#,##0.00";
        Finish(sheet, row);
    }

    /// <summary>Filter buttons on the header, frozen header row and fitted columns.</summary>
    private static void Finish(IXLWorksheet sheet, int lastRow)
    {
        sheet.Range(1, 1, Math.Max(lastRow - 1, 1), sheet.LastColumnUsed()?.ColumnNumber() ?? 1).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
    }

    private static string ChargeTypeName(ChargeType type) => type switch
    {
        ChargeType.Opening => ReportTexts.ChargeType_Opening,
        ChargeType.LateFee => ReportTexts.ChargeType_LateFee,
        _ => ReportTexts.ChargeType_Maintenance,
    };

    private static string StatusName(PaymentProofStatus status) => status switch
    {
        PaymentProofStatus.Approved => ReportTexts.Status_Approved,
        PaymentProofStatus.Rejected => ReportTexts.Status_Rejected,
        PaymentProofStatus.Voided => ReportTexts.Status_Voided,
        PaymentProofStatus.Historical => ReportTexts.Status_Historical,
        _ => ReportTexts.Status_Pending,
    };

    private static string IsoMonth(int period) =>
        (period / 100).ToString("0000", CultureInfo.InvariantCulture) + "-" + (period % 100).ToString("00", CultureInfo.InvariantCulture);

    public static byte[] DelinquencyPdf(DelinquencyReport report, DateTimeOffset generatedLocal)
    {
        ArgumentNullException.ThrowIfNull(report);
        var profile = CondoProfile.Current;
        var primary = Color.FromHex(profile.Theme.Primary);

        return Document.Create(container => container.Page(page =>
        {
            Page(page, Format(ReportTexts.DelinquencyTitle, Date(report.Date)), generatedLocal, primary);
            page.Content().PaddingTop(8).Column(column =>
            {
                column.Spacing(10);
                if (report.Towers.Count == 0)
                {
                    column.Item().Text(ReportTexts.NoDelinquency);
                }

                foreach (var tower in report.Towers)
                {
                    column.Item().Text($"{profile.TowerLabel} {tower.Name}").Bold().FontSize(12).FontColor(primary);
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(90);
                            columns.RelativeColumn();
                            columns.ConstantColumn(55);
                            columns.ConstantColumn(80);
                        });
                        table.Header(header =>
                        {
                            foreach (var label in new[] { ReportTexts.Apartment, ReportTexts.OverdueMonths, ReportTexts.MonthCount, ReportTexts.Amount })
                            {
                                header.Cell().BorderBottom(1).PaddingVertical(2).Text(label).SemiBold();
                            }
                        });
                        foreach (var item in tower.Rows)
                        {
                            table.Cell().PaddingVertical(2).Text(item.IsFrozen ? $"{item.Code} ({ReportTexts.Frozen})" : item.Code);
                            table.Cell().PaddingVertical(2).Text(string.Join(", ", item.Periods.Select(MonthName)));
                            table.Cell().PaddingVertical(2).AlignRight().Text(item.Periods.Count.ToString(Culture));
                            table.Cell().PaddingVertical(2).AlignRight().Text(Money(item.Amount));
                        }
                    });
                    column.Item().AlignRight().Text(Format(ReportTexts.TowerTotal, $"{profile.TowerLabel} {tower.Name}", tower.Rows.Count, Money(tower.Total))).SemiBold();
                }

                column.Item().PaddingTop(6).BorderTop(1).PaddingTop(4)
                    .Text(Format(ReportTexts.GrandTotal, report.DelinquentApartments, Money(report.Total))).Bold();
            });
        })).GeneratePdf();
    }

    public static byte[] FolioPdf(int period, IReadOnlyList<FolioLine> lines, DateTimeOffset generatedLocal)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var primary = Color.FromHex(CondoProfile.Current.Theme.Primary);
        var valid = lines.Where(l => !l.Voided).ToList();

        return Document.Create(container => container.Page(page =>
        {
            Page(page, Format(ReportTexts.FolioTitle, MonthName(period)), generatedLocal, primary);
            page.Content().PaddingTop(8).Column(column =>
            {
                column.Spacing(8);
                if (lines.Count == 0)
                {
                    column.Item().Text(ReportTexts.NoReceipts);
                    return;
                }

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(78);
                        columns.ConstantColumn(58);
                        columns.ConstantColumn(52);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(1.4f);
                        columns.ConstantColumn(62);
                    });
                    table.Header(header =>
                    {
                        foreach (var label in new[]
                                 {
                                     ReportTexts.ReceiptNumber, ReportTexts.Date, ReportTexts.Apartment, ReportTexts.ReceivedFrom,
                                     ReportTexts.Months, ReportTexts.Operation, ReportTexts.Amount,
                                 })
                        {
                            header.Cell().BorderBottom(1).PaddingVertical(2).Text(label).SemiBold();
                        }
                    });
                    foreach (var line in lines)
                    {
                        var color = line.Voided ? Colors.Red.Darken2 : Colors.Black;
                        table.Cell().PaddingVertical(2).Text(line.Number).FontColor(color);
                        table.Cell().PaddingVertical(2).Text(line.IssuedAtLocal.ToString("dd/MM/yyyy", Culture)).FontColor(color);
                        table.Cell().PaddingVertical(2).Text(line.ApartmentCode).FontColor(color);
                        table.Cell().PaddingVertical(2).Text(line.PayerName).FontColor(color);
                        table.Cell().PaddingVertical(2).Text(line.Voided ? ReportTexts.Voided : string.Join(", ", line.Periods.Select(ShortMonth))).FontColor(color);
                        table.Cell().PaddingVertical(2).Text(line.Operation).FontColor(color);
                        table.Cell().PaddingVertical(2).AlignRight().Text(Money(line.Amount)).FontColor(color);
                    }
                });
                column.Item().AlignRight()
                    .Text(Format(ReportTexts.FolioTotals, valid.Count, Money(valid.Sum(l => l.Amount)), lines.Count - valid.Count)).Bold();
            });
        })).GeneratePdf();
    }

    private static void Page(PageDescriptor page, string title, DateTimeOffset generatedLocal, Color primary)
    {
        var profile = CondoProfile.Current;
        page.Size(PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(t => t.FontSize(9));
        page.Header().Row(row =>
        {
            row.ConstantItem(40).Height(40).Svg(CondoProfile.LogoSvg);
            row.RelativeItem().PaddingLeft(8).Column(header =>
            {
                header.Item().Text(profile.DisplayName).Bold().FontSize(11);
                header.Item().Text(title).SemiBold().FontSize(13).FontColor(primary);
            });
        });
        page.Footer().Row(row =>
        {
            row.RelativeItem().Text(Format(ReportTexts.Generated, generatedLocal.ToString("dd/MM/yyyy HH:mm", Culture)))
                .FontSize(7).FontColor(Colors.Grey.Darken1);
            row.RelativeItem().AlignRight().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(7).FontColor(Colors.Grey.Darken1));
                text.Span(ReportTexts.Page);
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });
    }

    private static void Header(IXLWorksheet sheet, int row, params string[] labels)
    {
        for (var i = 0; i < labels.Length; i++)
        {
            sheet.Cell(row, i + 1).Value = labels[i];
        }

        var range = sheet.Range(row, 1, row, labels.Length);
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml(CondoProfile.Current.Theme.Primary);
        range.Style.Font.FontColor = XLColor.White;
    }

    private static string Money(decimal amount) => amount.ToString("C2", Culture);

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", Culture);

    private static string MonthName(int period) =>
        new DateTime(period / 100, period % 100, 1).ToString("MMMM yyyy", Culture).ToUpper(Culture);

    private static string ShortMonth(int period) =>
        new DateTime(period / 100, period % 100, 1).ToString("MMM yy", Culture).TrimEnd('.');

    private static string Format(string template, params object[] args) => string.Format(Culture, template, args);
}
