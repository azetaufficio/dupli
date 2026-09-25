using System.Text.Json;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Tools;
using Dupli.Contracts.Tools;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Updates;

/// <summary>
/// Activates the restic release desired by the server: install (sha256 + <c>restic version</c>, both inside
/// <see cref="IToolManager.EnsureInstalledAsync"/>), then persist <c>config\restic.json</c> and swap
/// <see cref="ActiveResticManifest"/>. No repository probe: the old binary stays in use until the install
/// succeeds; current + previous releases are kept on disk.
/// </summary>
public sealed class ResticUpdater(
    ActiveResticManifest active,
    IToolManager tools,
    AgentPaths paths,
    TimeProvider time,
    ILoggerFactory loggerFactory)
{
    private static readonly TimeSpan RetryAfterTransient = TimeSpan.FromMinutes(15);

    private readonly ILogger _logger = loggerFactory.CreateLogger<ResticUpdater>();
    private (string Version, DateTimeOffset NotBefore)? _backoff;
    private string? _permanentFailure;

    /// <summary>Why the desired release is not active; null when it is (reported in the heartbeat).</summary>
    public string? LastError { get; private set; }

    /// <summary>Manifest persisted by an earlier restic update, if any.</summary>
    public static ToolManifestDto? LoadPersisted(AgentPaths paths)
    {
        if (!File.Exists(paths.ResticStateFile))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ToolManifestDto>(File.ReadAllText(paths.ResticStateFile), AgentConfigLoader.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task TryActivateAsync(ToolManifestDto? desired, CancellationToken ct)
    {
        if (desired is null || desired.Version == active.Current.Version)
        {
            LastError = null;
            return;
        }
        if (_permanentFailure == desired.Version + desired.Sha256)
            return;
        if (_backoff is { } b && b.Version == desired.Version && time.GetUtcNow() < b.NotBefore)
            return;

        var previous = active.Current;
        try
        {
            await tools.EnsureInstalledAsync(desired, ct);

            var temp = paths.ResticStateFile + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(desired, AgentConfigLoader.JsonOptions), ct);
            File.Move(temp, paths.ResticStateFile, overwrite: true);
            active.Current = desired;
            LastError = null;
            _logger.LogInformation("restic {From} replaced by {To}", previous.Version, desired.Version);
            Cleanup(desired.Version, previous.Version);
        }
        catch (BackupException ex) when (ex.Kind == ErrorKind.Transient)
        {
            _backoff = (desired.Version, time.GetUtcNow() + RetryAfterTransient);
            LastError = $"restic {desired.Version}: {ex.Message} (retrying)";
            _logger.LogWarning(ex, "restic {Version} not activated yet, retrying in {Delay}", desired.Version, RetryAfterTransient);
        }
        catch (Exception ex) when (ex is BackupException or ArgumentException or IOException or InvalidDataException)
        {
            _permanentFailure = desired.Version + desired.Sha256;
            LastError = $"restic {desired.Version}: {ex.Message}";
            _logger.LogError(ex, "restic {Version} rejected, staying on {Current}", desired.Version, previous.Version);
        }
    }

    private void Cleanup(string keep, string keepPrevious)
    {
        foreach (var directory in Directory.EnumerateDirectories(paths.ResticTools))
        {
            var name = Path.GetFileName(directory);
            if (name == keep || name == keepPrevious || name.StartsWith('.'))
                continue;
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove old restic {Directory}", directory);
            }
        }
    }
}
