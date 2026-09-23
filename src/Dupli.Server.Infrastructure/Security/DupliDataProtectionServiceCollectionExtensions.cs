using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dupli.Server.Infrastructure.Security;

public enum DataProtectionKeyStore
{
    /// <summary>Key XML files in a directory (a volume outside the database). Keys are not encrypted at rest.</summary>
    FileSystem,

    /// <summary>One Azure Key Vault secret per key: encrypted at rest, access through RBAC.</summary>
    AzureKeyVault,
}

public enum KeyVaultCredentialKind
{
    /// <summary>Managed identity of the Azure resource the server runs on (system- or user-assigned).</summary>
    ManagedIdentity,

    /// <summary>App registration with a client secret.</summary>
    ClientSecret,
}

/// <summary>
/// The <c>DataProtection</c> section: where the key ring protecting escrowed secrets (repository passwords,
/// S3 keys) and the operator session cookies lives.
/// </summary>
public sealed class DataProtectionOptions
{
    public const string Section = "DataProtection";

    public DataProtectionKeyStore KeyStore { get; set; } = DataProtectionKeyStore.FileSystem;
    public FileSystemKeyStoreOptions FileSystem { get; set; } = new();
    public AzureKeyVaultKeyStoreOptions AzureKeyVault { get; set; } = new();
}

public sealed class FileSystemKeyStoreOptions
{
    /// <summary>Directory holding the key ring. Must be outside the database volume.</summary>
    public string Path { get; set; } = "/var/lib/dupli/keys";
}

/// <summary>
/// The identity needs to list, read and write secrets in the vault (role <c>Key Vault Secrets Officer</c>).
/// </summary>
public sealed class AzureKeyVaultKeyStoreOptions
{
    /// <summary>e.g. <c>https://dupli-kv.vault.azure.net/</c>.</summary>
    public string? VaultUri { get; set; }

    /// <summary>Prefix of the secret names, so one vault can hold several environments. Letters, digits, dashes.</summary>
    public string SecretPrefix { get; set; } = "dupli-dataprotection-";

    public KeyVaultCredentialKind Credential { get; set; } = KeyVaultCredentialKind.ManagedIdentity;
    public string? TenantId { get; set; }

    /// <summary>App registration client id; for <see cref="KeyVaultCredentialKind.ManagedIdentity"/> the
    /// user-assigned identity's client id, or empty for the system-assigned one.</summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public TokenCredential CreateCredential() => Credential switch
    {
        KeyVaultCredentialKind.ManagedIdentity => new ManagedIdentityCredential(string.IsNullOrWhiteSpace(ClientId)
            ? ManagedIdentityId.SystemAssigned
            : ManagedIdentityId.FromUserAssignedClientId(ClientId)),
        KeyVaultCredentialKind.ClientSecret => new ClientSecretCredential(TenantId, ClientId, ClientSecret),
        _ => throw new InvalidOperationException($"Unknown {DataProtectionOptions.Section}:AzureKeyVault:Credential '{Credential}'"),
    };
}

public static class DupliDataProtectionServiceCollectionExtensions
{
    public const string ApplicationName = "Dupli";

    public static IServiceCollection AddDupliDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DataProtectionOptions.Section).Get<DataProtectionOptions>() ?? new DataProtectionOptions();
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);

        switch (options.KeyStore)
        {
            case DataProtectionKeyStore.FileSystem:
                builder.PersistKeysToFileSystem(new DirectoryInfo(options.FileSystem.Path));
                break;
            case DataProtectionKeyStore.AzureKeyVault:
                var repository = CreateKeyVaultRepository(options.AzureKeyVault);
                services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
                break;
            default:
                throw new InvalidOperationException($"Unknown {DataProtectionOptions.Section}:KeyStore '{options.KeyStore}'");
        }

        return services;
    }

    private static KeyVaultXmlRepository CreateKeyVaultRepository(AzureKeyVaultKeyStoreOptions o)
    {
        const string prefix = $"{DataProtectionOptions.Section}:AzureKeyVault";
        if (!Uri.TryCreate(o.VaultUri, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{prefix}:VaultUri must be an https URL");
        if (!KeyVaultXmlRepository.IsValidPrefix(o.SecretPrefix))
            throw new InvalidOperationException($"{prefix}:SecretPrefix may contain only letters, digits and dashes (max 64)");
        if (o.Credential == KeyVaultCredentialKind.ClientSecret
            && (string.IsNullOrWhiteSpace(o.TenantId) || string.IsNullOrWhiteSpace(o.ClientId) || string.IsNullOrWhiteSpace(o.ClientSecret)))
            throw new InvalidOperationException($"{prefix}: Credential ClientSecret requires TenantId, ClientId and ClientSecret");

        return new KeyVaultXmlRepository(new SecretClient(vaultUri, o.CreateCredential()), o.SecretPrefix);
    }
}
