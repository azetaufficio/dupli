using Dupli.Server.Domain.Monitoring;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Dupli.Server.Infrastructure.Notifications;

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;

    /// <summary>None, StartTls or SslOnConnect.</summary>
    public string Security { get; set; } = "StartTls";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "dupli@localhost";

    /// <summary>No longer used: recipients come from each operator's notification preferences. Kept only so a
    /// leftover setting can be detected and warned about at startup.</summary>
    public List<string> To { get; set; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

public sealed class SmtpNotificationChannel(IOptions<SmtpOptions> options, ILogger<SmtpNotificationChannel> logger)
    : INotificationChannel
{
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task SendAsync(Notification notification, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.IsConfigured)
            throw new InvalidOperationException("SMTP is not configured (Notifications:Smtp)");
        if (recipients.Count == 0)
            return;

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(o.From));
        foreach (var to in recipients)
            message.To.Add(MailboxAddress.Parse(to));
        message.Subject = notification.Subject;
        message.Body = new TextPart("plain") { Text = notification.Body };

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host!, o.Port, Enum.Parse<SecureSocketOptions>(o.Security, ignoreCase: true), cancellationToken);
        if (!string.IsNullOrEmpty(o.Username))
            await client.AuthenticateAsync(o.Username, o.Password ?? "", cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        logger.LogInformation("Notification sent: {Subject}", notification.Subject);
    }
}
