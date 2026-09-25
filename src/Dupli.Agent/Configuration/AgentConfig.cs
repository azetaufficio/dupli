using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Secrets;
using Dupli.Contracts.Tools;

namespace Dupli.Agent.Configuration;

/// <summary>
/// Local <c>config\agent.json</c>. Never carries secret values: business credentials (repository,
/// PostgreSQL) are fetched per job from the server (<see cref="Dupli.Agent.Core.Secrets.JobCredentials"/>),
/// never persisted here or anywhere else on disk.
/// </summary>
public sealed record AgentConfig
{
    public required string AgentName { get; init; }
    public string Host { get; init; } = Environment.MachineName;
    public required RepositoryConfig Repository { get; init; }
    public required ToolManifestDto ResticManifest { get; init; }
    public RetryConfig Retry { get; init; } = new();

    /// <summary>Overrides RESTIC_CACHE_DIR. Defaults to <see cref="AgentPaths.Cache"/>.</summary>
    public string? ResticCacheDir { get; init; }

    /// <summary>Set at enrollment: the agent only ever runs driven by the server, never standalone.</summary>
    public ServerConfig? Server { get; init; }

    /// <summary>Local trust settings for agent updates. Never changed by the server.</summary>
    public UpdateConfig Update { get; init; } = new();
}

public sealed record UpdateConfig
{
    /// <summary>Reject agent packages without a valid Authenticode signature (Windows only).</summary>
    public bool RequireSignature { get; init; }

    /// <summary>Accepted signer certificate thumbprints (SHA-1 hex). Empty = any valid, trusted signature.</summary>
    public IReadOnlyList<string> SignerThumbprints { get; init; } = [];
}

public sealed record ServerConfig
{
    public required string Url { get; init; }
    public required string AgentId { get; init; }
    public int PollIntervalSeconds { get; init; } = 30;

    /// <summary>Name of the secret holding the AgentSecret in the local secret store.</summary>
    public string AgentSecretName { get; init; } = SecretNames.AgentSecret;
}

/// <summary>Secret names written by enrollment. The only one still persisted locally: everything else is
/// fetched just in time per job (<see cref="Dupli.Agent.Core.Secrets.JobCredentials"/>) and never stored.</summary>
public static class SecretNames
{
    public const string AgentSecret = "agent-secret";
}

/// <summary>
/// Either <see cref="Raw"/> (a full restic repository string, e.g. <c>s3:https://host/bucket/prefix</c>)
/// or the individual S3 fields, from which the repository string is composed. Never carries credentials:
/// those come from a <see cref="Dupli.Agent.Core.Secrets.JobCredentials"/> at <see cref="RepositoryConfigExtensions.Resolve"/> time.
/// </summary>
public sealed record RepositoryConfig
{
    public string? Raw { get; init; }
    public string? Endpoint { get; init; }
    public string? Bucket { get; init; }
    public string? Prefix { get; init; }
    public string? Region { get; init; }
}

public sealed record RetryConfig
{
    public int MaxRetries { get; init; } = 3;
    public int BaseDelaySeconds { get; init; } = 30;
}

public static class RepositoryConfigExtensions
{
    /// <summary>Builds the restic repository string and the backend environment from config + this job's credentials.</summary>
    public static RepositoryTarget Resolve(this RepositoryConfig config, JobCredentials credentials, string? resticCacheDir)
    {
        var repository = config.Raw ?? BuildS3Repository(config);
        var password = credentials.RepositoryPassword
            ?? throw new InvalidOperationException("Job credentials did not include a repository password");

        var env = new Dictionary<string, string>();
        if (credentials.AccessKeyId is { } accessKeyId)
            env["AWS_ACCESS_KEY_ID"] = accessKeyId;
        if (credentials.SecretAccessKey is { } secretAccessKey)
            env["AWS_SECRET_ACCESS_KEY"] = secretAccessKey;
        if (credentials.SessionToken is { } sessionToken)
            env["AWS_SESSION_TOKEN"] = sessionToken;
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
