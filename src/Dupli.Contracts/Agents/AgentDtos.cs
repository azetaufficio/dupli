using Dupli.Contracts.Tools;

namespace Dupli.Contracts.Agents;

/// <summary>First contact of a freshly installed agent, authenticated by a single-use enrollment token.</summary>
public sealed record RegisterAgentRequest
{
    public required string EnrollmentToken { get; init; }

    /// <summary>Stable machine identifier (MachineGuid on Windows). Binds the agent to one machine.</summary>
    public required string MachineId { get; init; }

    public required string Hostname { get; init; }
    public required string OsVersion { get; init; }
    public required string AgentVersion { get; init; }

    /// <summary>restic release platform (<c>windows_amd64</c>, <c>linux_amd64</c>, <c>linux_arm64</c>). Windows when omitted.</summary>
    public string? Platform { get; init; }
}

/// <summary>
/// Everything an agent needs to operate. Carries secrets: the agent stores them with DPAPI
/// immediately and never writes them to plain files or logs.
/// </summary>
public sealed class RegisterAgentResponse
{
    public required string AgentId { get; init; }
    public required string AgentSecret { get; init; }
    public required RepositoryDto Repository { get; init; }
    public required string RepositoryPassword { get; init; }
    public required string S3AccessKeyId { get; init; }
    public required string S3SecretAccessKey { get; init; }
    public required ToolManifestDto ResticManifest { get; init; }
    public int PollIntervalSeconds { get; init; } = 30;

    public override string ToString() => $"RegisterAgentResponse({AgentId})";
}

/// <summary>S3 location of the agent's restic repository (one repository per VM).</summary>
public sealed record RepositoryDto
{
    public required string Endpoint { get; init; }
    public required string Bucket { get; init; }
    public required string Prefix { get; init; }
    public string? Region { get; init; }
}

public sealed class AgentTokenRequest
{
    public required string AgentId { get; init; }
    public required string AgentSecret { get; init; }

    public override string ToString() => $"AgentTokenRequest({AgentId})";
}

public sealed record AgentTokenResponse
{
    public required string AccessToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed class RotateSecretResponse
{
    public required string AgentSecret { get; init; }

    public override string ToString() => "RotateSecretResponse";
}

public sealed record HeartbeatRequest
{
    public required string Hostname { get; init; }
    public required string Version { get; init; }
    public string? ResticVersion { get; init; }
    public required string OsVersion { get; init; }
    public DateTimeOffset? LastBackupAt { get; init; }
    public IReadOnlyList<string> RunningJobs { get; init; } = [];
    public long? FreeDiskSpace { get; init; }
}

public sealed record HeartbeatResponse
{
    public required DateTimeOffset ServerTime { get; init; }
    public int PollIntervalSeconds { get; init; } = 30;
}
