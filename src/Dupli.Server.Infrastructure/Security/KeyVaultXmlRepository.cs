using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Dupli.Server.Infrastructure.Security;

/// <summary>
/// Data Protection key ring stored as Azure Key Vault secrets, one secret per key XML element. Key Vault encrypts
/// the secrets at rest and gates access with RBAC, so no file share holds the keys. Data Protection never deletes
/// keys, so neither does this repository.
/// </summary>
public sealed partial class KeyVaultXmlRepository(SecretClient client, string secretPrefix) : IXmlRepository
{
    private const string ContentType = "application/xml";

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();
        foreach (var properties in client.GetPropertiesOfSecrets())
        {
            if (!properties.Name.StartsWith(secretPrefix, StringComparison.OrdinalIgnoreCase) || properties.Enabled == false)
                continue;
            elements.Add(XElement.Parse(client.GetSecret(properties.Name, null, CancellationToken.None).Value.Value));
        }
        return new ReadOnlyCollection<XElement>(elements);
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var secret = new KeyVaultSecret(SecretName(friendlyName), element.ToString(SaveOptions.DisableFormatting));
        secret.Properties.ContentType = ContentType;
        client.SetSecret(secret);
    }

    /// <summary>Secret names allow only letters, digits and dashes (max 127).</summary>
    private string SecretName(string? friendlyName)
    {
        var name = InvalidNameChars().Replace(string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString() : friendlyName, "-");
        var full = secretPrefix + name;
        return full.Length <= 127 ? full : full[..127];
    }

    internal static bool IsValidPrefix(string prefix) => !InvalidNameChars().IsMatch(prefix) && prefix.Length <= 64;

    [GeneratedRegex("[^0-9A-Za-z-]")]
    private static partial Regex InvalidNameChars();
}
