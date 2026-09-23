using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Secrets;
using Dupli.Contracts.Policies;
using Dupli.Contracts.Tools;

namespace Dupli.Agent.Configuration;

/// <summary>
/// Local <c>config\agent.json</c>. Never carries secret values: only the names of secrets
/// resolved through <see cref="ISecretStore"/> at run time.
/// </summary>
public sealed record AgentConfig
{
    public required string AgentName { get; init; }
    public string Host { get; init; } = Environment.MachineName;
    public required RepositoryConfig Repository { get; init; }
    public required ToolManifestDto ResticManifest { get; init; }
    public IReadOnlyList<ScheduledPolicy> Policies { get; init; } = [];
    public RetryConfig Retry { get; init; } = new();

    /// <summary>Overrides RESTIC_CACHE_DIR. Defaults to <see cref="AgentPaths.Cache"/>.</summary>
    public string? ResticCacheDir { get; init; }

    /// <summary>Set after enrollment: the agent is driven by the server and local <see cref="Policies"/> are not scheduled.</summary>
    public ServerConfig? Server { get; init; }
}

public sealed record ServerConfig
{
    public required string Url { get; init; }
    public required string AgentId { get; init; }
    public int PollIntervalSeconds { get; init; } = 30;

    /// <summary>Name of the secret holding the AgentSecret in the local secret store.</summary>
    public string AgentSecretName { get; init; } = SecretNames.AgentSecret;
}

/// <summary>Secret names written by enrollment.</summary>
public static class SecretNames
{
    public const string AgentSecret = "agent-secret";
    public const string RepositoryPassword = "repo-password";
    public const string S3AccessKey = "s3-access-key";
    public const string S3SecretKey = "s3-secret-key";
}

/// <summary>
/// Either <see cref="Raw"/> (a full restic repository string, e.g. <c>s3:https://host/bucket/prefix</c>)
/// or the individual S3 fields, from which the repository string is composed.
/// </summary>
public sealed record RepositoryConfig
{
    public string? Raw { get; init; }
    public string? Endpoint { get; init; }
    public string? Bucket { get; init; }
    public string? Prefix { get; init; }
    public string? Region { get; init; }

    public required string PasswordSecret { get; init; }
    public string? AccessKeySecret { get; init; }
    public string? SecretKeySecret { get; init; }
}

public sealed record ScheduledPolicy
{
    public required PolicySpecDto Policy { get; init; }

    /// <summary>Standard 5-field cron expression, evaluated with <see cref="TimeZone"/>.</summary>
    public required string Cron { get; init; }

    public string TimeZone { get; init; } = "Europe/Rome";
}

public sealed record RetryConfig
{
    public int MaxRetries { get; init; } = 3;
    public int BaseDelaySeconds { get; init; } = 30;
}

public static class RepositoryConfigExtensions
{
    /// <summary>Builds the restic repository string and the backend environment from config + secrets.</summary>
    public static RepositoryTarget Resolve(this RepositoryConfig config, ISecretStore secrets, string? resticCacheDir)
    {
        var repository = config.Raw ?? BuildS3Repository(config);
        var password = secrets.GetRequired(config.PasswordSecret);

        var env = new Dictionary<string, string>();
        if (config.AccessKeySecret is not null)
            env["AWS_ACCESS_KEY_ID"] = secrets.GetRequired(config.AccessKeySecret);
        if (config.SecretKeySecret is not null)
            env["AWS_SECRET_ACCESS_KEY"] = secrets.GetRequired(config.SecretKeySecret);
        if (!string.IsNullOrWhiteSpace(config.Region))
            env["AWS_DEFAULT_REGION"] = config.Region;
        if (!string.IsNullOrEmpty(resticCacheDir))
            env["RESTIC_CACHE_DIR"] = resticCacheDir;

        return new RepositoryTarget(repository, password, env);
    }

    private static string BuildS3Repository(RepositoryConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Bucket))
            throw new InvalidOperationException(
                "Repository configuration must set either 'raw' or both 'endpoint' and 'bucket'");

        var path = string.IsNullOrWhiteSpace(config.Prefix)
            ? config.Bucket
            : $"{config.Bucket}/{config.Prefix.Trim('/')}";
        return $"s3:{config.Endpoint.TrimEnd('/')}/{path}";
    }
}
