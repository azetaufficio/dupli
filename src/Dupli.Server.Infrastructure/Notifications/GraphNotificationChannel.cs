using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Dupli.Server.Domain.Monitoring;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Infrastructure.Notifications;

public enum Office365CredentialKind
{
    /// <summary>App registration with a client secret.</summary>
    ClientSecret,

    /// <summary>App registration with a certificate (PFX, or PEM holding certificate and key).</summary>
    Certificate,

    /// <summary>Managed identity of the Azure resource the server runs on (system- or user-assigned).</summary>
    ManagedIdentity,
}

/// <summary>
/// Office 365 mail through Microsoft Graph, app-only. The identity needs the <c>Mail.Send</c> application
/// permission; scope it to the sender mailbox with an application access policy.
/// </summary>
public sealed class Office365Options
{
    public Office365CredentialKind Credential { get; set; } = Office365CredentialKind.ClientSecret;
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string GraphEndpoint { get; set; } = "https://graph.microsoft.com/";
    public string? TenantId { get; set; }

    /// <summary>App registration client id; for <see cref="Office365CredentialKind.ManagedIdentity"/> the
    /// user-assigned identity's client id, or empty for the system-assigned one.</summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }

    /// <summary>UPN or object id of the mailbox that sends the mail.</summary>
    public string? From { get; set; }

    /// <summary>No longer used: recipients come from each operator's notification preferences. Kept only so a
    /// leftover setting can be detected and warned about at startup.</summary>
    public List<string> To { get; set; } = [];
    public bool SaveToSentItems { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(From) && Credential switch
        {
            Office365CredentialKind.ClientSecret => HasApp && !string.IsNullOrWhiteSpace(ClientSecret),
            Office365CredentialKind.Certificate => HasApp && !string.IsNullOrWhiteSpace(CertificatePath),
            Office365CredentialKind.ManagedIdentity => true,
            _ => false,
        };

    public string GraphScope => new Uri(new Uri(GraphEndpoint), ".default").ToString();
    public string GraphBaseUrl => new Uri(new Uri(GraphEndpoint), "v1.0").ToString();

    private bool HasApp => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);

    public TokenCredential CreateCredential()
    {
        var authority = new Uri(Instance);
        return Credential switch
        {
            Office365CredentialKind.ClientSecret => new ClientSecretCredential(TenantId, ClientId, ClientSecret,
                new ClientSecretCredentialOptions { AuthorityHost = authority }),
            Office365CredentialKind.Certificate => new ClientCertificateCredential(TenantId, ClientId, LoadCertificate(),
                new ClientCertificateCredentialOptions { AuthorityHost = authority }),
            Office365CredentialKind.ManagedIdentity => new ManagedIdentityCredential(string.IsNullOrWhiteSpace(ClientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(ClientId)),
            _ => throw new InvalidOperationException($"Unknown Office365 credential '{Credential}'"),
        };
    }

    public GraphServiceClient CreateGraphClient() => new(CreateCredential(), [GraphScope], GraphBaseUrl);

    private X509Certificate2 LoadCertificate() =>
        Path.GetExtension(CertificatePath) is ".pem" or ".crt"
            ? X509Certificate2.CreateFromPemFile(CertificatePath!)
            : X509CertificateLoader.LoadPkcs12FromFile(CertificatePath!, CertificatePassword);
}

public sealed class GraphNotificationChannel(
    IOptions<Office365Options> options,
    Func<Office365Options, GraphServiceClient> clientFactory,
    ILogger<GraphNotificationChannel> logger) : INotificationChannel
{
    // Built on first send, so a bad certificate path surfaces as a failed (retried) notification, not a startup crash.
    // The client's credential caches the token and refreshes it before expiry.
    private readonly Lazy<GraphServiceClient> _client = new(() => clientFactory(options.Value));

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task SendAsync(Notification notification, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.IsConfigured)
            throw new InvalidOperationException("Office 365 is not configured (Notifications:Office365)");
        if (recipients.Count == 0)
            return;

        var body = new SendMailPostRequestBody
        {
            Message = new Message
            {
                Subject = notification.Subject,
                Body = new ItemBody { ContentType = BodyType.Text, Content = notification.Body },
                ToRecipients = [.. recipients.Select(to => new Recipient { EmailAddress = new EmailAddress { Address = to } })],
            },
            SaveToSentItems = o.SaveToSentItems,
        };

        // Failures throw ODataError, so the alert evaluator retries on its next tick.
        await _client.Value.Users[o.From].SendMail.PostAsync(body, cancellationToken: cancellationToken);

        logger.LogInformation("Notification sent: {Subject}", notification.Subject);
    }
}
