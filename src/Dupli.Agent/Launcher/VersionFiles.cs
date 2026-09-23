using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dupli.Agent.Configuration;
using Dupli.Contracts.Agents;

namespace Dupli.Agent.Launcher;

/// <summary>An installed agent version: <c>versions\&lt;ver&gt;\dupli-agent.exe</c> and its expected sha256.</summary>
public sealed record InstalledVersion
{
    public required string Version { get; init; }
    public required string ExePath { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>
/// <c>versions\current.json</c>: what the Launcher starts. <see cref="Probation"/> is set right after a switch:
/// until the new version reports healthy, a crash or a timeout rolls back to <see cref="Previous"/>. It survives
/// a Launcher restart (reboot during probation): the window restarts with the next child.
/// </summary>
public sealed record CurrentVersionFile
{
    public required string Version { get; init; }
    public required string ExePath { get; init; }
    public required string Sha256 { get; init; }
    public InstalledVersion? Previous { get; init; }
    public bool Probation { get; init; }

    public InstalledVersion ToInstalled() => new() { Version = Version, ExePath = ExePath, Sha256 = Sha256 };

    public static CurrentVersionFile From(InstalledVersion v, InstalledVersion? previous = null, bool probation = false) => new()
    {
        Version = v.Version,
        ExePath = v.ExePath,
        Sha256 = v.Sha256,
        Previous = previous,
        Probation = probation,
    };
}

/// <summary>Written by the agent after its first healthy cycle; read by the Launcher during probation.</summary>
public sealed record HealthFile
{
    public required string Version { get; init; }
    public required int Pid { get; init; }
    public required DateTimeOffset At { get; init; }
}

/// <summary>Outcome of the last update (reported in the heartbeat) + versions that must not be retried.</summary>
public sealed record UpdateStateFile
{
    public UpdateStatusDto? Last { get; init; }
    public IReadOnlyList<string> FailedVersions { get; init; } = [];
}

/// <summary>Atomic JSON files under <c>versions\</c> shared by installer, Launcher and agent.</summary>
public sealed class VersionFiles(AgentPaths paths)
{
    public const string ExeName = "dupli-agent";

    private static readonly JsonSerializerOptions JsonOptions = new(AgentConfigLoader.JsonOptions)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string CurrentPath => paths.CurrentVersionFile;
    public string PendingPath => Path.Combine(paths.Versions, "pending.json");
    public string HealthPath => Path.Combine(paths.Versions, "health.json");
    public string UpdateStatePath => Path.Combine(paths.Versions, "update-state.json");

    public static string ExeFileName => OperatingSystem.IsWindows() ? ExeName + ".exe" : ExeName;

    public string VersionDirectory(string version) => Path.Combine(paths.Versions, version);

    public CurrentVersionFile? ReadCurrent() => Read<CurrentVersionFile>(CurrentPath);
    public void WriteCurrent(CurrentVersionFile value) => Write(CurrentPath, value);

    public InstalledVersion? ReadPending() => Read<InstalledVersion>(PendingPath);
    public void WritePending(InstalledVersion value) => Write(PendingPath, value);
    public void DeletePending() => File.Delete(PendingPath);

    public HealthFile? ReadHealth() => Read<HealthFile>(HealthPath);
    public void WriteHealth(HealthFile value) => Write(HealthPath, value);
    public void DeleteHealth() => File.Delete(HealthPath);

    public UpdateStateFile ReadUpdateState() => Read<UpdateStateFile>(UpdateStatePath) ?? new UpdateStateFile();
    public void WriteUpdateState(UpdateStateFile value) => Write(UpdateStatePath, value);

    public static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is invalid: {ex.Message}", ex);
        }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
