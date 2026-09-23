using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Amazon.Runtime;
using Amazon.S3;
using Dupli.Agent.Cli;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Secrets;
using Dupli.Agent.Server;
using Dupli.Contracts;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Policies;
using Dupli.Contracts.Tools;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Jobs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace Dupli.IntegrationTests;

/// <summary>
/// Server in-process (real PostgreSQL) + agent components (real restic, RustFS S3): enrollment,
/// run-now backup, idempotent redelivery, repository check, restore test, restart, and crash recovery (Interrupted).
/// </summary>
public sealed class AgentServerEndToEndTests : IAsyncLifetime
{
    private const string AdminKey = "it-admin-key";
    private const string S3AccessKey = "dupli-it";
    private const string S3SecretKey = "dupli-it-secret";
    private const string ResticVersion = "0.19.1";

    private static readonly Dictionary<string, (string Asset, string Sha256)> ResticAssets = new()
    {
        ["osx-arm64"] = ("restic_0.19.1_darwin_arm64.bz2", "7be0a144ccc377880f294204aa271d76e4b79554b42a751151d425ce6ebac143"),
        ["linux-x64"] = ("restic_0.19.1_linux_amd64.bz2", "f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c"),
        ["win-x64"] = ("restic_0.19.1_windows_amd64.zip", "da948ad707ed690426473aaba2046cd61f8f90f6f0e7dab6be0d5796531de67d"),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dupli-it", Guid.NewGuid().ToString("N"));
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly IContainer _s3 = new ContainerBuilder("rustfs/rustfs:1.0.0")
        .WithEnvironment("RUSTFS_ACCESS_KEY", S3AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", S3SecretKey)
        .WithPortBinding(9000, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/health")))
        .Build();

    private ServerFactory _server = null!;
    private readonly ILoggerFactory _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information));

    private string S3Endpoint => $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(9000)}";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_db.StartAsync(), _s3.StartAsync());
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(S3AccessKey, S3SecretKey),
            new AmazonS3Config { ServiceURL = S3Endpoint, ForcePathStyle = true });
        await s3.PutBucketAsync("backups");

        _server = new ServerFactory(_db.GetConnectionString(), Path.Combine(_root, "server"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await Task.WhenAll(_db.DisposeAsync().AsTask(), _s3.DisposeAsync().AsTask());
        _loggers.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Enrolled_agent_backs_up_on_demand_and_recovers_from_a_crash()
    {
        var admin = _server.CreateClient();
        admin.DefaultRequestHeaders.Add(AuthConstants.AdminKeyHeader, AdminKey);

        // Operator: storage target, pending agent with its S3 key, enrollment token, policy.
        var storage = await Read<StorageTargetDto>(await admin.PostAsJsonAsync("/api/admin/storage-targets",
            new CreateStorageTargetRequest("rustfs", S3Endpoint, "backups", "us-east-1"), DupliJson.Options));
        var agent = await Read<AgentDto>(await admin.PostAsJsonAsync("/api/admin/agents", new CreateAgentRequest
        {
            Name = "vm-it", StorageTargetId = storage.Id, StoragePrefix = "agents/vm-it",
            S3AccessKeyId = S3AccessKey, S3SecretAccessKey = S3SecretKey,
        }, DupliJson.Options));
        var token = await Read<EnrollmentTokenDto>(await admin.PostAsync($"/api/admin/agents/{agent.Id}/enrollment-tokens", null));

        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "invoice.txt"), "INV-001");
        var policy = await Read<PolicyDto>(await admin.PostAsJsonAsync($"/api/admin/agents/{agent.Id}/policies", new PolicyRequest
        {
            Name = "nightly", Cron = "0 2 * * *",
            Sources = [new DirectorySourceDto { SourceId = "data", Paths = [data] }],
        }, DupliJson.Options));

        // Agent: enrollment stores every secret in the secret store, none in agent.json.
        var paths = new AgentPaths(Path.Combine(_root, "agent"));
        var secrets = new MemorySecretStore();
        var enrolled = await AgentEnrollment.EnrollAsync("http://localhost", token.Token, paths, secrets,
            _server.CreateClient(), NullLogger.Instance, CancellationToken.None);
        Assert.Equal(agent.Id.ToString(), enrolled.Server!.AgentId);
        Assert.DoesNotContain(S3SecretKey, await File.ReadAllTextAsync(paths.ConfigFile));
        Assert.Equal(S3SecretKey, secrets.Get(SecretNames.S3SecretKey));

        // The mirror serves the Windows build; the test host needs its own platform's restic.
        var config = enrolled with { ResticManifest = LocalResticManifest() };
        var (loop, runtime, ledger) = CreateAgent(paths, config, secrets);

        // Run now → assigned → backed up → reported.
        var job = await Read<JobDto>(await admin.PostAsync($"/api/admin/policies/{policy.Id}/run", null));
        await loop.RunOnceAsync(CancellationToken.None);

        var finished = await Read<JobDto>(await admin.GetAsync($"/api/admin/jobs/{job.Id}"));
        Assert.Equal(JobState.Succeeded, finished.State);
        var run = Assert.Single(await Read<List<RunDto>>(await admin.GetAsync($"/api/admin/runs?policyId={policy.Id}")));
        var snapshotId = Assert.Single(run.Items).SnapshotId;

        var snapshots = await runtime.Engine.ListSnapshotsAsync(runtime.Repository, [BackupTags.Policy(policy.Id.ToString())], CancellationToken.None);
        Assert.Contains(snapshots, s => s.Id == snapshotId);
        Assert.Equal(LedgerState.Reported, ledger.GetState(job.Id.ToString()));

        var status = await Read<AgentDto>(await admin.GetAsync($"/api/admin/agents/{agent.Id}"));
        Assert.True(status.Online);
        Assert.NotNull(status.LastBackupAt);

        // Repository check through the same channel.
        var check = await Read<JobDto>(await admin.PostAsJsonAsync($"/api/admin/agents/{agent.Id}/jobs",
            new RunSystemJobRequest(JobType.RepositoryCheck), DupliJson.Options));
        await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(JobState.Succeeded, (await Read<JobDto>(await admin.GetAsync($"/api/admin/jobs/{check.Id}"))).State);

        // Restore test: the sampled file is restored, verified and reported per item; nothing is left behind.
        var restoreTest = await Read<JobDto>(await admin.PostAsJsonAsync($"/api/admin/agents/{agent.Id}/jobs",
            new RunSystemJobRequest(JobType.RestoreTest), DupliJson.Options));
        await loop.RunOnceAsync(CancellationToken.None);
        var tested = await Read<JobDto>(await admin.GetAsync($"/api/admin/jobs/{restoreTest.Id}"));
        Assert.Equal(JobState.Succeeded, tested.State);
        var verified = Assert.Single(tested.Items);
        Assert.EndsWith("invoice.txt", verified.Item);
        Assert.Equal(snapshotId, verified.SnapshotId);
        Assert.Equal("INV-001".Length, verified.BytesProcessed);
        Assert.False(Directory.Exists(Path.Combine(paths.Tmp, "restore-test", restoreTest.Id.ToString())));

        // Restart: reported as succeeded first, then the process would exit.
        var restart = await Read<JobDto>(await admin.PostAsJsonAsync($"/api/admin/agents/{agent.Id}/jobs",
            new RunSystemJobRequest(JobType.RestartAgent), DupliJson.Options));
        await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(JobState.Succeeded, (await Read<JobDto>(await admin.GetAsync($"/api/admin/jobs/{restart.Id}"))).State);
        Assert.Equal(1, _restarter.Restarts);

        // Crash mid-job: the job is Running locally when the process dies.
        var crashed = await Read<JobDto>(await admin.PostAsync($"/api/admin/policies/{policy.Id}/run", null));
        var client = CreateClient(config, secrets);
        var delivered = Assert.Single(await client.GetJobsAsync(CancellationToken.None));
        Assert.True(ledger.TryBegin(delivered.JobId, delivered.Type, DateTimeOffset.UtcNow));
        ledger.MarkRunning(delivered.JobId, DateTimeOffset.UtcNow);
        await client.StartedAsync(delivered.JobId, DateTimeOffset.UtcNow, CancellationToken.None);

        // Restarted agent: reports Interrupted and does not execute the job again.
        var (restarted, _, _) = CreateAgent(paths, config, secrets);
        await restarted.RunOnceAsync(CancellationToken.None);

        var interrupted = await Read<JobDto>(await admin.GetAsync($"/api/admin/jobs/{crashed.Id}"));
        Assert.Equal(JobState.Failed, interrupted.State);
        Assert.StartsWith("Interrupted", interrupted.Error);
        Assert.Single(await runtime.Engine.ListSnapshotsAsync(runtime.Repository, [BackupTags.Policy(policy.Id.ToString())], CancellationToken.None));
    }

    private readonly CountingRestarter _restarter = new();

    private (ServerAgentLoop Loop, AgentRuntime Runtime, JobLedger Ledger) CreateAgent(AgentPaths paths, AgentConfig config, ISecretStore secrets)
    {
        var runtime = AgentRuntimeFactory.Build(paths, config, _loggers, TimeProvider.System, secrets);
        var ledger = new JobLedger(paths.LedgerFile);
        var executor = new JobExecutor(runtime, ledger, TimeProvider.System, _loggers.CreateLogger<JobExecutor>());
        var loop = new ServerAgentLoop(CreateClient(config, secrets), ledger, executor, config, paths, TimeProvider.System,
            _restarter, _loggers.CreateLogger<ServerAgentLoop>());
        return (loop, runtime, ledger);
    }

    private ServerClient CreateClient(AgentConfig config, ISecretStore secrets) =>
        new(_server.CreateClient(), config.Server!, secrets, TimeProvider.System, _loggers.CreateLogger<ServerClient>());

    private static ToolManifestDto LocalResticManifest()
    {
        var (asset, sha) = ResticAssets[RuntimeInformation.RuntimeIdentifier];
        return new ToolManifestDto
        {
            Version = ResticVersion,
            DownloadUrl = $"https://github.com/restic/restic/releases/download/v{ResticVersion}/{asset}",
            Sha256 = sha,
        };
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(DupliJson.Options))!;
    }

    private sealed class ServerFactory(string connectionString, string dataDir) : WebApplicationFactory<DupliServerOptions>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Dupli", connectionString);
            builder.UseSetting("Dupli:RunBackgroundServices", "false");
            builder.UseSetting("Dupli:DataProtectionKeysPath", Path.Combine(dataDir, "keys"));
            builder.UseSetting("Dupli:ToolMirrorPath", Path.Combine(dataDir, "tools"));
            builder.UseSetting("Dupli:Admin:ApiKey", AdminKey);
        }
    }

    private sealed class CountingRestarter : IAgentRestarter
    {
        public int Restarts { get; private set; }

        public void Restart() => Restarts++;
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

        public void Set(string name, string value) => _values[name] = value;
    }
}
