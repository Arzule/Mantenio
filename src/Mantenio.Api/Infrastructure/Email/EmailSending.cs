using MailKit.Net.Smtp;
using MailKit.Security;
using Mantenio.Shared.Condos;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mantenio.Api.Infrastructure.Email;

public sealed record EmailMessage(string To, string ToName, string Subject, string TextBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// SMTP settings ("Email" section). Production: Gmail (smtp.gmail.com:587, StartTls, app password as a secret).
/// Local: the Mailpit container started by the Aspire AppHost (no TLS, no credentials).
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

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
        mime.Body = new TextPart("plain") { Text = message.TextBody };

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

        if (string.IsNullOrWhiteSpace(section[nameof(EmailOptions.Host)]))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Email:Host is not configured.");
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
