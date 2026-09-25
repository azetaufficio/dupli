using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Core.Restic;
using Dupli.Agent.Core.Tools;
using Dupli.Contracts.Tools;
using Dupli.Server.Api;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Infrastructure.Security;
using Dupli.Server.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Restore;

// Dupli.Agent (namespace, from Agent.Core) would otherwise shadow the entity.
using Agent = Dupli.Server.Domain.Agents.Agent;

/// <summary>
/// Read-only view of an agent's restic repository, run by the server with the escrowed credentials
/// (<c>--no-lock</c>): snapshot list and browse work with the VM offline or busy. Restores stay agent jobs.
/// </summary>
public sealed class RepositoryBrowser : IResticBinaryProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<DupliServerOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RepositoryBrowser> _logger;
    private readonly ResticBackupEngine _engine;
    private readonly ResticToolManager _tools;
    private readonly SemaphoreSlim _concurrency;

    public RepositoryBrowser(
        IServiceScopeFactory scopes,
        IOptions<DupliServerOptions> options,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
        _scopes = scopes;
        _options = options;
        _cache = cache;
        _logger = loggerFactory.CreateLogger<RepositoryBrowser>();
        var runner = new ProcessRunner(loggerFactory.CreateLogger<ProcessRunner>());
        _engine = new ResticBackupEngine(this, runner, loggerFactory.CreateLogger<ResticBackupEngine>());
        _tools = new ResticToolManager(
            Path.Combine(Path.GetFullPath(options.Value.ToolMirrorPath), ".server", "restic"),
            new HttpClient(), runner, loggerFactory.CreateLogger<ResticToolManager>());
        _concurrency = new SemaphoreSlim(Math.Max(1, options.Value.Restore.MaxConcurrentListings));
    }

    private RestoreOptions Options => _options.Value.Restore;

    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(Guid agentId, bool refresh, CancellationToken ct)
    {
        var key = ("snapshots", agentId);
        if (!refresh && _cache.TryGetValue(key, out IReadOnlyList<SnapshotInfo>? cached))
            return Task.FromResult(cached!);

        return RunAsync(agentId, async (repository, token) =>
        {
            var snapshots = (await _engine.ListSnapshotsAsync(repository, [], token))
                .OrderByDescending(s => s.Time)
                .ToList();
            _cache.Set(key, (IReadOnlyList<SnapshotInfo>)snapshots, Options.SnapshotCacheDuration);
            return (IReadOnlyList<SnapshotInfo>)snapshots;
        }, ct);
    }

    public Task<IReadOnlyList<SnapshotNode>> ListDirectoryAsync(Guid agentId, string snapshotId, string directory, CancellationToken ct) =>
        RunAsync(agentId, (repository, token) => _engine.ListDirectoryAsync(repository, snapshotId, directory, token), ct);

    /// <summary>Clears the cached snapshot list of an agent: called after its repository credentials change.</summary>
    public void Invalidate(Guid agentId) => _cache.Remove(("snapshots", agentId));

    /// <summary>Removes the agent's restic cache directory and its cached snapshot list, called when the agent
    /// itself is deleted. The repository in the bucket is untouched.</summary>
    public void DeleteCache(Guid agentId)
    {
        Invalidate(agentId);
        var directory = CacheDirectory(agentId);
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete the restic cache directory of agent {AgentId}", agentId);
        }
    }

    /// <summary>
    /// Read-only snapshot listing with candidate S3 credentials, used to validate a storage credentials update
    /// before it is saved. Throws <see cref="ApiException"/> (422, restic's own error) on failure.
    /// </summary>
    public async Task VerifyCredentialsAsync(Agent agent, string accessKeyId, string secretAccessKey, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var repository = BuildRepository(agent, accessKeyId, secretAccessKey,
            protector.Unprotect(agent.RepositoryPasswordProtected), CacheDirectory(agent.Id), ResolveEndpoint(agent.StorageTarget!.Endpoint));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.ListingTimeout);
        await _concurrency.WaitAsync(timeout.Token);
        try
        {
            await _engine.ListSnapshotsAsync(repository, [], timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw ApiException.UnprocessableEntity(
                $"Repository {repository} did not answer within {Options.ListingTimeout.TotalSeconds:0}s");
        }
        catch (BackupException ex)
        {
            _logger.LogWarning(ex, "Storage credentials verification for agent {AgentId} failed", agent.Id);
            throw ApiException.UnprocessableEntity($"Could not list the repository with these credentials: {ex.Message}");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>The snapshot (by full or short id), or null when the repository has none with that id.</summary>
    public async Task<SnapshotInfo?> FindSnapshotAsync(Guid agentId, string snapshotId, CancellationToken ct)
    {
        static SnapshotInfo? Match(IEnumerable<SnapshotInfo> all, string id) =>
            all.FirstOrDefault(s => s.Id == id || s.ShortId == id);

        return Match(await ListSnapshotsAsync(agentId, refresh: false, ct), snapshotId)
            ?? Match(await ListSnapshotsAsync(agentId, refresh: true, ct), snapshotId);
    }

    private async Task<T> RunAsync<T>(Guid agentId, Func<RepositoryTarget, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var repository = await GetRepositoryAsync(agentId, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.ListingTimeout);
        await _concurrency.WaitAsync(timeout.Token);
        try
        {
            return await action(repository, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw ApiException.BadGateway(
                $"Repository {repository} did not answer within {Options.ListingTimeout.TotalSeconds:0}s");
        }
        catch (BackupException ex)
        {
            _logger.LogWarning(ex, "Listing repository of agent {AgentId} failed", agentId);
            throw ApiException.BadGateway($"Repository listing failed: {ex.Message}");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<RepositoryTarget> GetRepositoryAsync(Guid agentId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DupliDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var agent = await db.Agents.AsNoTracking().Include(a => a.StorageTarget)
            .SingleOrDefaultAsync(a => a.Id == agentId, ct) ?? throw ApiException.NotFound("Agent");
        return BuildRepository(agent, protector, CacheDirectory(agentId), ResolveEndpoint(agent.StorageTarget!.Endpoint));
    }

    /// <summary>Server-side override for an S3 endpoint agents reach directly (a private endpoint, or a
    /// container network name in the dev stack). Null when the agent's own endpoint also works for the server.</summary>
    private string? ResolveEndpoint(string endpoint) =>
        Options.EndpointOverrides
            .FirstOrDefault(o => string.Equals(o.From.TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.To;

    /// <summary>The agent's own (escrowed) credentials.</summary>
    internal static RepositoryTarget BuildRepository(Agent agent, SecretProtector protector, string cacheDirectory, string? endpoint = null) =>
        BuildRepository(agent, agent.S3AccessKeyId, protector.Unprotect(agent.S3SecretKeyProtected),
            protector.Unprotect(agent.RepositoryPasswordProtected), cacheDirectory, endpoint);

    /// <summary>Explicit credentials, e.g. a candidate S3 key not yet saved (<see cref="VerifyCredentialsAsync"/>).</summary>
    internal static RepositoryTarget BuildRepository(
        Agent agent, string accessKeyId, string secretAccessKey, string repositoryPassword, string cacheDirectory, string? endpoint = null)
    {
        var storage = agent.StorageTarget ?? throw new InvalidOperationException("Storage target not loaded");
        var path = string.IsNullOrWhiteSpace(agent.StoragePrefix) ? storage.Bucket : $"{storage.Bucket}/{agent.StoragePrefix.Trim('/')}";
        var env = new Dictionary<string, string>
        {
            ["AWS_ACCESS_KEY_ID"] = accessKeyId,
            ["AWS_SECRET_ACCESS_KEY"] = secretAccessKey,
            ["RESTIC_CACHE_DIR"] = cacheDirectory,
        };
        if (!string.IsNullOrWhiteSpace(storage.Region))
            env["AWS_DEFAULT_REGION"] = storage.Region;
        return new RepositoryTarget(
            $"s3:{(endpoint ?? storage.Endpoint).TrimEnd('/')}/{path}",
            repositoryPassword,
            env,
            readOnly: true);
    }

    private string CacheDirectory(Guid agentId)
    {
        var root = Options.CachePath
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_options.Value.ToolMirrorPath).TrimEnd(Path.DirectorySeparatorChar))!, "cache");
        return Path.Combine(Path.GetFullPath(root), agentId.ToString("N"));
    }

    /// <summary>restic for the server itself: explicit path, or the current release of the server platform from the mirror.</summary>
    async Task<string> IResticBinaryProvider.GetPathAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Options.ResticPath))
            return Options.ResticPath;

        var platform = ResticPlatform.Current;
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DupliDbContext>();
        var release = await db.Releases.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Product == ReleaseMirror.ResticProduct && r.Platform == platform && r.IsCurrent, ct)
            ?? throw ApiException.Unavailable($"No current restic release for the server platform {platform}: register one under Releases");

        var exe = _tools.GetExecutablePath(release.Version);
        if (File.Exists(exe))
            return exe;

        var mirror = scope.ServiceProvider.GetRequiredService<ReleaseMirror>();
        var asset = await mirror.GetAssetAsync(ReleaseMirror.ResticProduct, release.Version, platform, ct)
            ?? throw ApiException.Unavailable($"restic {release.Version} for {platform} is not available");
        return await _tools.EnsureInstalledAsync(new ToolManifestDto
        {
            Version = release.Version,
            DownloadUrl = new Uri(asset.Path).AbsoluteUri,
            Sha256 = release.Sha256,
        }, ct);
    }
}
