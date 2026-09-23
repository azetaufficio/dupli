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
    public List<string> To { get; set; } = [];

    /// <summary>Non-blank recipients (compose passes an empty <c>Smtp__To__0</c> when unset).</summary>
    public IEnumerable<string> Recipients => To.Where(t => !string.IsNullOrWhiteSpace(t));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && Recipients.Any();
}

public sealed class SmtpNotificationChannel(IOptions<SmtpOptions> options, ILogger<SmtpNotificationChannel> logger)
    : INotificationChannel
{
    public async Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.IsConfigured)
        {
            logger.LogWarning("SMTP not configured, notification dropped: {Subject}", notification.Subject);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(o.From));
        foreach (var to in o.Recipients)
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
