using System.Net;
using System.Net.Mail;
using Erp.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Notifications;

public sealed class SmtpOptions
{
    public const string SectionName = "Notifications:Smtp";

    /// <summary>Off by default: messages are only logged until a server is configured (on-prem: customer SMTP).</summary>
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "no-reply@erp.local";
}

/// <summary>Default sender: writes the notification to the log (subject and recipient only — bodies may contain secrets).</summary>
public sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Email notification (not sent — SMTP disabled): to {To}, subject {Subject}", message.To, message.Subject);
        return Task.CompletedTask;
    }
}

public sealed class SmtpNotificationSender(IOptions<SmtpOptions> options) : INotificationSender
{
    public async Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.EnableSsl };
        if (!string.IsNullOrEmpty(settings.UserName))
        {
            client.Credentials = new NetworkCredential(settings.UserName, settings.Password);
        }

        using var mail = new MailMessage(settings.From, message.To, message.Subject, message.Body);
        await client.SendMailAsync(mail, cancellationToken);
    }
}
