using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Tools;
using Dupli.Agent.Core.Secrets;
using Dupli.Contracts;
using Dupli.Contracts.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Dupli.Agent.Server;

/// <summary>
/// Exchanges the single-use enrollment token for AgentId + secrets, stores every secret in the local
/// secret store (DPAPI) and writes <c>agent.json</c> in server mode. Secrets never touch the JSON file.
/// </summary>
public static class AgentEnrollment
{
    public const string AllowInsecureHttpVariable = "DUPLI_ALLOW_INSECURE_HTTP";

    /// <summary>Local test stacks (Aspire, containers) talk to the server over HTTP on a non-loopback host.</summary>
    private static bool AllowInsecureHttp =>
        string.Equals(Environment.GetEnvironmentVariable(AllowInsecureHttpVariable), "true", StringComparison.OrdinalIgnoreCase);

    public static async Task<AgentConfig> EnrollAsync(
        string serverUrl,
        string enrollmentToken,
        AgentPaths paths,
        ISecretStore secrets,
        HttpClient http,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        var baseUri = new Uri(serverUrl.TrimEnd('/') + "/");
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback && !AllowInsecureHttp)
            throw new InvalidOperationException(
                $"Refusing to enroll over plain HTTP: {serverUrl} (development only: set {AllowInsecureHttpVariable}=true)");

        using var response = await http.PostAsJsonAsync(new Uri(baseUri, "api/agents/register"), new RegisterAgentRequest
        {
            EnrollmentToken = enrollmentToken,
            MachineId = MachineIdentity.Get(),
            Hostname = Environment.MachineName,
            OsVersion = RuntimeInformation.OSDescription,
            AgentVersion = Configuration.AgentVersion.Current,
            Platform = ResticPlatform.Current,
        }, DupliJson.Options, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Enrollment rejected by {serverUrl}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");

        var registration = await response.Content.ReadFromJsonAsync<RegisterAgentResponse>(DupliJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("Empty enrollment response");

        secrets.Set(SecretNames.AgentSecret, registration.AgentSecret);
        secrets.Set(SecretNames.RepositoryPassword, registration.RepositoryPassword);
        secrets.Set(SecretNames.S3AccessKey, registration.S3AccessKeyId);
        secrets.Set(SecretNames.S3SecretKey, registration.S3SecretAccessKey);
        // Enrollment already delivers the current S3 key: nothing to apply after the fact.
        await StorageCredentialsStateFile.WriteAsync(paths, registration.S3CredentialsVersion, cancellationToken);

        // Keep local settings (retry, cache dir) of an existing install; server mode replaces the rest.
        var previous = File.Exists(paths.ConfigFile) ? AgentConfigLoader.Load(paths.ConfigFile) : null;
        var config = new AgentConfig
        {
            AgentName = Environment.MachineName,
            Host = previous?.Host ?? Environment.MachineName,
            Repository = new RepositoryConfig
            {
                Endpoint = registration.Repository.Endpoint,
                Bucket = registration.Repository.Bucket,
                Prefix = registration.Repository.Prefix,
                Region = registration.Repository.Region,
                PasswordSecret = SecretNames.RepositoryPassword,
                AccessKeySecret = SecretNames.S3AccessKey,
                SecretKeySecret = SecretNames.S3SecretKey,
            },
            ResticManifest = registration.ResticManifest,
            Retry = previous?.Retry ?? new RetryConfig(),
            ResticCacheDir = previous?.ResticCacheDir,
            Update = previous?.Update ?? new UpdateConfig(),
            Server = new ServerConfig
            {
                Url = baseUri.ToString().TrimEnd('/'),
                AgentId = registration.AgentId,
                PollIntervalSeconds = registration.PollIntervalSeconds,
            },
        };

        var temp = paths.ConfigFile + ".tmp";
        await File.WriteAllTextAsync(temp, AgentConfigLoader.Serialize(config), cancellationToken);
        File.Move(temp, paths.ConfigFile, overwrite: true);

        logger.LogInformation("Enrolled as agent {AgentId}; repository {Endpoint}/{Bucket}/{Prefix}",
            registration.AgentId, registration.Repository.Endpoint, registration.Repository.Bucket, registration.Repository.Prefix);
        return config;
    }
}

public static class MachineIdentity
{
    /// <summary>MachineGuid on Windows, /etc/machine-id on Linux, hostname as last resort (dev only).</summary>
    public static string Get()
    {
        if (OperatingSystem.IsWindows() && WindowsMachineGuid() is { } guid)
            return guid;
        if (File.Exists("/etc/machine-id"))
            return File.ReadAllText("/etc/machine-id").Trim();
        return $"host:{Environment.MachineName}";
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsMachineGuid()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }
}
