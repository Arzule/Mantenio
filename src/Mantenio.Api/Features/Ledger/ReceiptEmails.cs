using System.Globalization;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Api.Resources;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Condos;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Ledger;

/// <summary>Sends the receipt PDF to whoever uploaded the proof. Failures are logged, never thrown.</summary>
internal sealed partial class ReceiptEmails(AppDbContext db, IFileStorage storage, IEmailSender sender, ILogger<ReceiptEmails> logger)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);

    public async Task SendAsync(Receipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            var data = await (
                    from p in db.PaymentProofs.AsNoTracking()
                    join u in db.Users on p.SubmittedById equals u.Id
                    where p.Id == receipt.PaymentProofId
                    select new { u.Email, u.FirstName, u.LastName, p.ApprovedAmount })
                .SingleAsync(cancellationToken);
            var pdf = receipt.PdfObjectKey is null ? null : await storage.GetAsync(receipt.PdfObjectKey, cancellationToken);
            if (data.Email is null || pdf is null)
            {
                return;
            }

            var condo = CondoProfile.Current.DisplayName;
            var amount = (data.ApprovedAmount ?? 0).ToString("C2", Culture);
            await sender.SendAsync(new EmailMessage(
                data.Email,
                $"{data.FirstName} {data.LastName}",
                Format(ReceiptTexts.EmailSubject, data.FirstName, condo, receipt.Number, amount),
                Format(ReceiptTexts.EmailBody, data.FirstName, condo, receipt.Number, amount),
                [new EmailAttachment(Format(ReceiptTexts.AttachmentName, receipt.Number), "application/pdf", pdf)]), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSendFailed(ex, receipt.Number);
        }
    }

    private static string Format(string template, params object[] args) => string.Format(Culture, template, args);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not email receipt {ReceiptNumber}")]
    private partial void LogSendFailed(Exception exception, string receiptNumber);
}
