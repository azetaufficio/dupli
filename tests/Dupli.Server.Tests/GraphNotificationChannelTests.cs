using System.Net;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Authentication.Azure;

namespace Dupli.Server.Tests;

public sealed class GraphNotificationChannelTests
{
    private static readonly Office365Options Configured = new()
    {
        TenantId = "tenant-1",
        ClientId = "client-1",
        ClientSecret = "secret",
        From = "dupli@contoso.com",
        To = ["ops@contoso.com", ""],
    };

    [Fact]
    public async Task Sends_mail_as_the_configured_mailbox()
    {
        var handler = new FakeGraph();
        var credential = new FakeCredential();
        var channel = Channel(Configured, handler, credential);

        await channel.SendAsync(new Notification("subject 1", "body 1"), CancellationToken.None);

        Assert.Equal(["https://graph.microsoft.com/.default"], credential.Scopes);
        // The SDK serializes action parameters as "Message"/"SaveToSentItems"; Graph accepts either casing.
        var (uri, auth, json) = Assert.Single(handler.Mails);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/dupli%40contoso.com/sendMail", uri.AbsoluteUri);
        Assert.Equal("Bearer fake-token", auth);
        var message = json.RootElement.GetProperty("Message");
        Assert.Equal("subject 1", message.GetProperty("subject").GetString());
        Assert.Equal("text", message.GetProperty("body").GetProperty("contentType").GetString());
        var to = Assert.Single(message.GetProperty("toRecipients").EnumerateArray());
        Assert.Equal("ops@contoso.com", to.GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.False(json.RootElement.GetProperty("SaveToSentItems").GetBoolean());
    }

    [Theory]
    [InlineData(Office365CredentialKind.ClientSecret, typeof(ClientSecretCredential))]
    [InlineData(Office365CredentialKind.ManagedIdentity, typeof(ManagedIdentityCredential))]
    public void Credential_follows_configuration(Office365CredentialKind kind, Type expected)
    {
        var options = new Office365Options { Credential = kind, TenantId = "t", ClientId = "c", ClientSecret = "s" };
        Assert.IsType(expected, options.CreateCredential());
    }

    [Theory]
    [InlineData(Office365CredentialKind.ClientSecret, "t", "c", "s", null, true)]
    [InlineData(Office365CredentialKind.ClientSecret, "t", "c", null, null, false)]
    [InlineData(Office365CredentialKind.Certificate, "t", "c", null, "/cert.pfx", true)]
    [InlineData(Office365CredentialKind.Certificate, "t", "c", "s", null, false)]
    [InlineData(Office365CredentialKind.ManagedIdentity, null, null, null, null, true)]
    public void Required_settings_depend_on_the_credential(
        Office365CredentialKind kind, string? tenant, string? client, string? secret, string? cert, bool configured)
    {
        var options = new Office365Options
        {
            Credential = kind, TenantId = tenant, ClientId = client, ClientSecret = secret, CertificatePath = cert,
            From = "dupli@contoso.com", To = ["ops@contoso.com"],
        };
        Assert.Equal(configured, options.IsConfigured);
    }

    [Fact]
    public async Task Graph_error_is_thrown_so_the_alert_is_retried()
    {
        var handler = new FakeGraph { SendMailStatus = HttpStatusCode.Forbidden };
        var channel = Channel(Configured, handler, new FakeCredential());

        var ex = await Assert.ThrowsAsync<ODataError>(
            () => channel.SendAsync(new Notification("a", "b"), CancellationToken.None));
        Assert.Equal(403, ex.ResponseStatusCode);
        Assert.Equal("ErrorAccessDenied", ex.Error?.Code);
    }

    [Fact]
    public async Task Unconfigured_channel_drops_the_notification()
    {
        var handler = new FakeGraph();
        var credential = new FakeCredential();
        var channel = Channel(new Office365Options { TenantId = "t" }, handler, credential);

        await channel.SendAsync(new Notification("a", "b"), CancellationToken.None);

        Assert.Empty(credential.Scopes);
        Assert.Empty(handler.Mails);
    }

    [Theory]
    [InlineData(null, typeof(SmtpNotificationChannel))]
    [InlineData("Smtp", typeof(SmtpNotificationChannel))]
    [InlineData("Office365", typeof(GraphNotificationChannel))]
    public void Channel_is_selected_from_configuration(string? channel, Type expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Channel"] = channel })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddDupliNotifications(configuration)
            .BuildServiceProvider();

        Assert.IsType(expected, services.GetRequiredService<INotificationChannel>());
    }

    private static GraphNotificationChannel Channel(Office365Options options, FakeGraph handler, TokenCredential credential) =>
        new(Options.Create(options),
            o => new GraphServiceClient(new HttpClient(handler),
                new AzureIdentityAuthenticationProvider(credential, scopes: [o.GraphScope]), o.GraphBaseUrl),
            NullLogger<GraphNotificationChannel>.Instance);

    private sealed class FakeCredential : TokenCredential
    {
        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class FakeGraph : HttpMessageHandler
    {
        public List<(Uri Uri, string? Auth, JsonDocument Json)> Mails { get; } = [];
        public HttpStatusCode SendMailStatus { get; init; } = HttpStatusCode.Accepted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Mails.Add((request.RequestUri!, request.Headers.Authorization?.ToString(),
                JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct))));
            return new HttpResponseMessage(SendMailStatus)
            {
                Content = new StringContent("""{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
