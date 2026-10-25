using System.Globalization;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Api.Resources;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Condos;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Ledger;

/// <summary>
/// Sends the receipt PDF to whoever uploaded the proof or, when an admin registered it for a resident, to the
/// apartment's active users with an email (the admin can also send it by WhatsApp). Failures are logged, never thrown.
/// </summary>
internal sealed partial class ReceiptEmails(AppDbContext db, IFileStorage storage, IEmailSender sender, ILogger<ReceiptEmails> logger)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);

    public async Task SendAsync(Receipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            var proof = await db.PaymentProofs.AsNoTracking().SingleAsync(p => p.Id == receipt.PaymentProofId, cancellationToken);
            var recipients = await db.Users.AsNoTracking()
                .Where(u => u.Email != null && (proof.PayerName == null
                    ? u.Id == proof.SubmittedById
                    : u.IsActive && u.ApartmentId == proof.ApartmentId))
                .Select(u => new { u.Email, u.FirstName, u.LastName })
                .ToListAsync(cancellationToken);
            var pdf = receipt.PdfObjectKey is null ? null : await storage.GetAsync(receipt.PdfObjectKey, cancellationToken);
            if (recipients.Count == 0 || pdf is null)
            {
                return;
            }

            var condo = CondoProfile.Current.DisplayName;
            var amount = (proof.ApprovedAmount ?? 0).ToString("C2", Culture);
            foreach (var recipient in recipients)
            {
                await sender.SendAsync(new EmailMessage(
                    recipient.Email!,
                    $"{recipient.FirstName} {recipient.LastName}",
                    Format(ReceiptTexts.EmailSubject, recipient.FirstName, condo, receipt.Number, amount),
                    Format(ReceiptTexts.EmailBody, recipient.FirstName, condo, receipt.Number, amount),
                    [new EmailAttachment(LedgerService.ReceiptFileName(receipt.Number), "application/pdf", pdf)]), cancellationToken);
            }
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
