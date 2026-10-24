using Azure;
using Azure.Communication.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mantenio.Shared.Condos;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mantenio.Api.Infrastructure.Email;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(string To, string ToName, string Subject, string TextBody, IReadOnlyList<EmailAttachment>? Attachments = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Email settings ("Email" section). Production: Azure Communication Services (<see cref="AcsConnectionString"/>,
/// a secret). Any SMTP server also works (<see cref="Host"/>). Local: the Mailpit container started by the
/// Aspire AppHost (SMTP, no TLS, no credentials).
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Azure Communication Services connection string; when set it wins over SMTP.</summary>
    public string? AcsConnectionString { get; set; }

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "";

    /// <summary>Defaults to the condo display name.</summary>
    public string? FromName { get; set; }
}

internal sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName ?? CondoProfile.Current.DisplayName, settings.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.To));
        mime.Subject = message.Subject;
        var body = new BodyBuilder { TextBody = message.TextBody };
        foreach (var attachment in message.Attachments ?? [])
        {
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        mime.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security, cancellationToken);
        if (!string.IsNullOrEmpty(settings.UserName))
        {
            await client.AuthenticateAsync(settings.UserName, settings.Password ?? "", cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}

/// <summary>Azure Communication Services Email. The sender is a verified address of the linked email domain.</summary>
internal sealed class AcsEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private readonly EmailClient _client = new(options.Value.AcsConnectionString);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var content = new EmailContent(message.Subject) { PlainText = message.TextBody };
        var recipients = new EmailRecipients([new EmailAddress(message.To, message.ToName)]);
        var email = new Azure.Communication.Email.EmailMessage(options.Value.FromAddress, recipients, content);
        foreach (var attachment in message.Attachments ?? [])
        {
            email.Attachments.Add(new Azure.Communication.Email.EmailAttachment(
                attachment.FileName, attachment.ContentType, BinaryData.FromBytes(attachment.Content)));
        }

        // Returns once ACS accepts the message (not on delivery); a rejection throws RequestFailedException.
        await _client.SendAsync(WaitUntil.Started, email, cancellationToken);
    }
}

/// <summary>Development fallback when no SMTP server is configured: the email is written to the log.</summary>
internal sealed partial class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        LogEmail(message.To, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No SMTP server configured. Email to {To}: {Subject}\n{Body}")]
    private partial void LogEmail(string to, string subject, string body);
}

internal static class EmailSetup
{
    public static IServiceCollection AddMantenioEmail(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(EmailOptions.SectionName);
        services.AddOptions<EmailOptions>().Bind(section);
        services.AddScoped<AccountEmails>();

        if (!string.IsNullOrWhiteSpace(section[nameof(EmailOptions.AcsConnectionString)]))
        {
            services.AddSingleton<IEmailSender, AcsEmailSender>();
        }
        else if (string.IsNullOrWhiteSpace(section[nameof(EmailOptions.Host)]))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Email is not configured (Email:AcsConnectionString or Email:Host).");
            }

            services.AddSingleton<IEmailSender, LogEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }

        return services;
    }
}
