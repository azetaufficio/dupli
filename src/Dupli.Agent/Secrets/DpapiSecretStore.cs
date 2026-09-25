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
/// Windows (the supported platform): DPAPI (LocalMachine scope) file per secret under
/// <c>config\secrets\&lt;name&gt;.bin</c>, restricted to SYSTEM+Administrators.
/// Other OSes (test containers, development): for <see cref="SecretNames.AgentSecret"/> only,
/// <c>DUPLI_SECRET_&lt;NAME&gt;</c> environment variable first, then a file readable only by the agent's
/// user (0600 in a 0700 directory). There is no OS key store there, so the file is protected by permissions
/// only. The only secret this store ever persists is the agent secret: every business credential (repository,
/// PostgreSQL) is fetched per job and never written here.
/// </summary>
public sealed class DpapiSecretStore(AgentPaths paths, ILogger<DpapiSecretStore> logger) : ISecretStore
{
    public string? Get(string name)
    {
        ValidateName(name);
        if (!OperatingSystem.IsWindows())
        {
            if (name == SecretNames.AgentSecret
                && Environment.GetEnvironmentVariable($"DUPLI_SECRET_{name.ToUpperInvariant()}") is { } fromEnvironment)
                return fromEnvironment;
            var plain = PlainSecretPath(name);
            return File.Exists(plain) ? File.ReadAllText(plain) : null;
        }

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
        {
            SetPermissionProtected(name, value);
            return;
        }

        Directory.CreateDirectory(paths.Secrets);
        RestrictToSystemAndAdministrators(paths.Secrets);

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        Array.Clear(plainBytes);

        var file = SecretPath(name);
        File.WriteAllBytes(file, protectedBytes);
        logger.LogInformation("Secret '{Name}' stored at {Path}", name, file);
    }

    [UnsupportedOSPlatform("windows")]
    private void SetPermissionProtected(string name, string value)
    {
        Directory.CreateDirectory(paths.Secrets);
        File.SetUnixFileMode(paths.Secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var file = PlainSecretPath(name);
        var temp = file + ".tmp";
        // Created with 0600 before any byte is written, then moved into place atomically.
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var writer = new StreamWriter(stream))
            writer.Write(value);
        File.Move(temp, file, overwrite: true);
        logger.LogWarning("Secret '{Name}' stored at {Path}, protected by file permissions only (no DPAPI on this OS)", name, file);
    }

    /// <summary>Removes a secret, if present. Used once at startup to purge everything but <c>agent-secret</c>.</summary>
    public void Delete(string name)
    {
        ValidateName(name);
        var file = OperatingSystem.IsWindows() ? SecretPath(name) : PlainSecretPath(name);
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>Names of every secret currently stored (not their values).</summary>
    public IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(paths.Secrets))
            return [];
        var suffix = OperatingSystem.IsWindows() ? ".bin" : ".secret";
        return Directory.EnumerateFiles(paths.Secrets, $"*{suffix}")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
    }

    private string SecretPath(string name) => Path.Combine(paths.Secrets, $"{name}.bin");

    private string PlainSecretPath(string name) => Path.Combine(paths.Secrets, $"{name}.secret");

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
