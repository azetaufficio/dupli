using Azure;
using Azure.Security.KeyVault.Secrets;
using Dupli.Server.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Tests;

public sealed class KeyVaultDataProtectionTests
{
    [Fact]
    public void Key_ring_in_key_vault_survives_a_restart()
    {
        var vault = new FakeSecretClient();
        vault.Values["other-app-secret"] = "not xml";

        var protectedValue = Provider(vault).CreateProtector("test").Protect("repo-password");

        var keySecret = Assert.Single(vault.Values.Keys, k => k.StartsWith("dupli-dataprotection-key-", StringComparison.Ordinal));
        Assert.Matches("^[0-9A-Za-z-]+$", keySecret);
        Assert.Equal("application/xml", vault.ContentTypes[keySecret]);

        // A new process with the same vault reads the same key.
        Assert.Equal("repo-password", Provider(vault).CreateProtector("test").Unprotect(protectedValue));
    }

    [Theory]
    [InlineData(null, "ManagedIdentity", "VaultUri")]
    [InlineData("http://kv.vault.azure.net/", "ManagedIdentity", "VaultUri")]
    [InlineData("https://kv.vault.azure.net/", "ClientSecret", "TenantId")]
    public void Invalid_key_vault_configuration_fails_at_startup(string? vaultUri, string credential, string message)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:KeyStore"] = "AzureKeyVault",
            ["DataProtection:AzureKeyVault:VaultUri"] = vaultUri,
            ["DataProtection:AzureKeyVault:Credential"] = credential,
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDupliDataProtection(configuration));
        Assert.Contains(message, error.Message);
    }

    private static IDataProtectionProvider Provider(FakeSecretClient vault)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(DupliDataProtectionServiceCollectionExtensions.ApplicationName);
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new KeyVaultXmlRepository(vault, "dupli-dataprotection-"));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private sealed class FakeSecretClient : SecretClient
    {
        public Dictionary<string, string> Values { get; } = [];
        public Dictionary<string, string?> ContentTypes { get; } = [];

        public override Pageable<SecretProperties> GetPropertiesOfSecrets(CancellationToken cancellationToken = default) =>
            Pageable<SecretProperties>.FromPages([Page<SecretProperties>.FromValues(
                [.. Values.Keys.Select(name => SecretModelFactory.SecretProperties(name: name))], null, null!)]);

        public override Response<KeyVaultSecret> GetSecret(string name, string? version = null, CancellationToken cancellationToken = default) =>
            Response.FromValue(SecretModelFactory.KeyVaultSecret(SecretModelFactory.SecretProperties(name: name), Values[name]), null!);

        public override Response<KeyVaultSecret> SetSecret(KeyVaultSecret secret, CancellationToken cancellationToken = default)
        {
            Values[secret.Name] = secret.Value;
            ContentTypes[secret.Name] = secret.Properties.ContentType;
            return Response.FromValue(secret, null!);
        }
    }
}
