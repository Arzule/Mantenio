using System.Globalization;
using ClosedXML.Excel;
using Mantenio.Api.Resources;
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
                    column.Item().AlignRight().Text(Format(ReportTexts.TowerTotal, tower.Name, tower.Rows.Count, Money(tower.Total))).SemiBold();
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
