using System.Text.Json;
using Dupli.Agent.Configuration;

namespace Dupli.Agent.Server;

/// <summary>
/// Local record of which S3 credentials version this agent has applied (<c>config\storage-credentials.json</c>),
/// mirroring the <c>restic.json</c> state file the update loop keeps. Written atomically (tmp + move), like
/// every other agent state file, so a crash mid-write never leaves a half-written file behind.
/// </summary>
public sealed record StorageCredentialsState
{
    public required int Version { get; init; }
}

public static class StorageCredentialsStateFile
{
    /// <summary>Version applied so far. 1 (the version every agent enrolls with) when the file is missing or
    /// unreadable: an agent built before this feature, or one whose enrollment has not run yet.</summary>
    public static int Read(AgentPaths paths)
    {
        if (!File.Exists(paths.StorageCredentialsFile))
            return 1;
        try
        {
            return JsonSerializer.Deserialize<StorageCredentialsState>(File.ReadAllText(paths.StorageCredentialsFile), AgentConfigLoader.JsonOptions)?.Version ?? 1;
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    public static async Task WriteAsync(AgentPaths paths, int version, CancellationToken ct)
    {
        var temp = paths.StorageCredentialsFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new StorageCredentialsState { Version = version }, AgentConfigLoader.JsonOptions), ct);
        File.Move(temp, paths.StorageCredentialsFile, overwrite: true);
    }
}
