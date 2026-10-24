using System.Globalization;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Resources;
using Mantenio.Shared.Condos;

namespace Mantenio.Api.Infrastructure.Email;

/// <summary>Account emails (all carry a 6-digit code). Texts live in Resources/EmailTexts.resx.</summary>
internal sealed partial class AccountEmails(IEmailSender sender, ILogger<AccountEmails> logger)
{
    public Task<bool> SendTwoFactorCodeAsync(AppUser user, string code, CancellationToken cancellationToken) =>
        SendAsync(user, EmailTexts.TwoFactorSubject, EmailTexts.TwoFactorBody, code, cancellationToken);

    public Task<bool> SendEmailConfirmationCodeAsync(AppUser user, string code, CancellationToken cancellationToken) =>
        SendAsync(user, EmailTexts.ConfirmEmailSubject, EmailTexts.ConfirmEmailBody, code, cancellationToken);

    public Task<bool> SendPasswordResetCodeAsync(AppUser user, string code, CancellationToken cancellationToken) =>
        SendAsync(user, EmailTexts.ResetPasswordSubject, EmailTexts.ResetPasswordBody, code, cancellationToken);

    /// <summary>Returns false (and logs) when the SMTP server fails, so the endpoint can answer 503.</summary>
    private async Task<bool> SendAsync(AppUser user, string subject, string body, string code, CancellationToken cancellationToken)
    {
        var condo = CondoProfile.Current.DisplayName;
        var message = new EmailMessage(
            user.Email!,
            user.FullName,
            string.Format(CultureInfo.CurrentCulture, subject, condo),
            string.Format(CultureInfo.CurrentCulture, body, user.FirstName, condo, code));
        try
        {
            await sender.SendAsync(message, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSendFailed(ex, user.Id);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not send an account email to user {UserId}")]
    private partial void LogSendFailed(Exception exception, Guid userId);
}
