using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Secrets;

/// <summary>
/// Windows: DPAPI (LocalMachine scope) file per secret under <c>config\secrets\&lt;name&gt;.bin</c>,
/// restricted to SYSTEM+Administrators. Any other OS: read-only env fallback for local development,
/// never a plaintext file.
/// </summary>
public sealed class DpapiSecretStore(AgentPaths paths, ILogger<DpapiSecretStore> logger) : ISecretStore
{
    public string? Get(string name)
    {
        ValidateName(name);
        if (!OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable($"DUPLI_SECRET_{name.ToUpperInvariant()}");

        var file = SecretPath(name);
        if (!File.Exists(file))
            return null;

        var protectedBytes = File.ReadAllBytes(file);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            Array.Clear(plainBytes);
        }
    }

    public void Set(string name, string value)
    {
        ValidateName(name);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Secrets are only persisted with DPAPI on Windows. For local development set " +
                $"the environment variable DUPLI_SECRET_{name.ToUpperInvariant()} instead.");

        Directory.CreateDirectory(paths.Secrets);
        RestrictToSystemAndAdministrators(paths.Secrets);

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        Array.Clear(plainBytes);

        var file = SecretPath(name);
        File.WriteAllBytes(file, protectedBytes);
        logger.LogInformation("Secret '{Name}' stored at {Path}", name, file);
    }

    private string SecretPath(string name) => Path.Combine(paths.Secrets, $"{name}.bin");

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException($"Invalid secret name '{name}'", nameof(name));
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToSystemAndAdministrators(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.SetAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
