using System.Globalization;
using Mantenio.Api.Features.Reports;
using Mantenio.Api.Features.Settings;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Resources;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Common;
using Mantenio.Shared.Condos;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Jobs;

/// <summary>
/// Day 1: emails last month's summary to the board members the founder listed: totals, the folio PDF, the period report
/// (Excel) and the audit fingerprint. Several people keep a copy outside the app, so a later deletion or edit of the
/// records can be noticed. Sent once per month (an audit event marks it).
/// </summary>
internal sealed partial class BoardSummaryJob(
    AppDbContext db, CondoClock clock, TimeProvider time, PeriodReportService reports, IEmailSender sender, ILogger<BoardSummaryJob> logger)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);

    /// <summary>Sends the summary of the previous month; returns how many board members got it (0 when already sent or nobody is listed).</summary>
    public async Task<int> SendAsync(CancellationToken cancellationToken)
    {
        var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, cancellationToken);
        var recipients = settings.BoardEmailList;
        var month = clock.CurrentPeriod.Previous();
        var marker = $"board-summary-{month.Value}";
        if (recipients.Count == 0 || await db.AuditEvents.AnyAsync(e => e.EntityType == AuditEntities.Job && e.EntityId == marker, cancellationToken))
        {
            return 0;
        }

        var report = await reports.BuildAsync(month, month, cancellationToken);
        var folio = await ReportEndpoints.FolioLinesAsync(db, month.Value, cancellationToken);
        var valid = folio.Where(l => !l.Voided).ToList();
        var condo = CondoProfile.Current.DisplayName;
        var monthName = new DateTime(month.Year, month.Month, 1).ToString("MMMM yyyy", Culture);
        var subject = Format(EmailTexts.BoardSummarySubject, condo, monthName);
        var body = Format(EmailTexts.BoardSummaryBody, condo, monthName, Money(valid.Sum(l => l.Amount)), valid.Count, folio.Count - valid.Count,
            Money(report.DelinquencyAtClose.Total), report.DelinquencyAtClose.DelinquentApartments,
            report.CloseDate.ToString("dd/MM/yyyy", Culture), report.Fingerprint.Events, report.Fingerprint.Hash);
        EmailAttachment[] attachments =
        [
            new(ReportFiles.FolioFileName(month.Value), ReportFiles.PdfContentType, ReportFiles.FolioPdf(month.Value, folio, clock.Now)),
            new(ReportFiles.PeriodFileName(month.Value, month.Value), ReportFiles.ExcelContentType, ReportFiles.PeriodExcel(report)),
        ];

        var sent = 0;
        foreach (var email in recipients)
        {
            try
            {
                await sender.SendAsync(new EmailMessage(email, email, subject, body, attachments), cancellationToken);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSendFailed(ex, email);
            }
        }

        db.AuditEvents.Add(new AuditEvent(AuditEntities.Job, marker, "BoardSummarySent", AuditSource.System, null, time.GetUtcNow(),
            newValues: AuditLog.Json(new { Period = month.Value, Recipients = sent, report.Fingerprint.Events, report.Fingerprint.Hash })));
        await db.SaveChangesAsync(cancellationToken);
        return sent;
    }

    private static string Money(decimal amount) => amount.ToString("C2", Culture);

    private static string Format(string template, params object[] args) => string.Format(Culture, template, args);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not email the board summary to {Email}")]
    private partial void LogSendFailed(Exception exception, string email);
}
