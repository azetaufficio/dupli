using Amazon.Runtime;
using Amazon.S3;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Postgres;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Core.Secrets;
using Dupli.Agent.Core.Snapshots;
using Dupli.Agent.Core.Tests.Infrastructure;
using Dupli.Agent.Core.Verification;
using Dupli.Contracts.Policies;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace Dupli.Agent.Core.Tests;

/// <summary>
/// Directory + PostgreSQL (container) backed up to S3 (RustFS container) with the real restic and pg_dump,
/// then restored and verified. Requires Docker and a local pg_dump.
/// </summary>
[Collection(ResticCollection.Name)]
public sealed class PolicyRunnerEndToEndTests(ResticFixture restic) : IAsyncLifetime
{
    private const string PgPassword = "pg-secret";
    private const string S3AccessKey = "dupli-test";
    private const string S3SecretKey = "dupli-test-secret";

    private static readonly string? PgBin = new[]
        {
            Environment.GetEnvironmentVariable("DUPLI_TEST_PG_BIN"),
            "/opt/homebrew/opt/libpq/bin",
            "/usr/lib/postgresql/18/bin",
            "/usr/bin",
        }
        .FirstOrDefault(d => d is not null && File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump")));

    private readonly TempDir _tmp = new();
    private PostgreSqlContainer? _pg;
    private IContainer? _s3;

    private string S3Endpoint => $"http://{_s3!.Hostname}:{_s3.GetMappedPublicPort(9000)}";

    public async Task InitializeAsync()
    {
        if (PgBin is null)
            return;
        _pg = new PostgreSqlBuilder("postgres:18-alpine").WithPassword(PgPassword).Build();
        _s3 = new ContainerBuilder("rustfs/rustfs:1.0.0")
            .WithEnvironment("RUSTFS_ACCESS_KEY", S3AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", S3SecretKey)
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/health")))
            .Build();
        await Task.WhenAll(_pg.StartAsync(), _s3.StartAsync());

        // Buckets are provisioned by the operator; restic would retry a missing bucket for ~15 minutes.
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(S3AccessKey, S3SecretKey),
            new AmazonS3Config { ServiceURL = S3Endpoint, ForcePathStyle = true });
        await s3.PutBucketAsync("backups");

        await using var conn = new NpgsqlConnection(_pg.GetConnectionString());
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     "CREATE DATABASE app_one", "CREATE DATABASE app_two", "CREATE DATABASE skipped",
                     "CREATE ROLE app_user LOGIN",
                 })
            await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();

        var appOne = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Database = "app_one" };
        await using var c1 = new NpgsqlConnection(appOne.ConnectionString);
        await c1.OpenAsync();
        await new NpgsqlCommand("CREATE TABLE items(id int primary key, name text); INSERT INTO items VALUES (1,'alpha'),(2,'beta')", c1)
            .ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Full_policy_backup_to_s3_and_restore()
    {
        Skip.If(PgBin is null, "pg_dump not available");

        var data = Directory.CreateDirectory(_tmp.Combine("data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "invoice.txt"), "INV-001");
        var missing = _tmp.Combine("does-not-exist");

        var pgBuilder = new NpgsqlConnectionStringBuilder(_pg!.GetConnectionString());
        var policy = new PolicySpecDto
        {
            PolicyId = "pol-1",
            Name = "Nightly",
            Sources =
            [
                new DirectorySourceDto { SourceId = "files", Paths = [data] },
                new DirectorySourceDto { SourceId = "broken", Paths = [missing] },
                new PostgresSourceDto
                {
                    SourceId = "pg",
                    Host = pgBuilder.Host!,
                    Port = pgBuilder.Port,
                    Username = pgBuilder.Username!,
                    PasswordSecret = "pg-password",
                    ExcludeDatabases = ["skipped"],
                    BinDirectory = PgBin,
                },
            ],
        };

        var repo = new RepositoryTarget(
            $"s3:{S3Endpoint}/backups/vm-001",
            "repo-password",
            new Dictionary<string, string>
            {
                ["AWS_ACCESS_KEY_ID"] = S3AccessKey,
                ["AWS_SECRET_ACCESS_KEY"] = S3SecretKey,
                ["RESTIC_CACHE_DIR"] = _tmp.Combine("cache"),
            });

        var engine = restic.CreateEngine();
        var runner = CreateRunner(engine, new MemorySecrets { ["pg-password"] = PgPassword });

        var result = await runner.RunAsync(policy, repo, "vm-001", default);

        // Broken source fails permanently, everything else succeeds independently.
        Assert.Equal(RunStatus.Failed, result.Status);
        var broken = Assert.Single(result.Items, i => i.SourceId == "broken");
        Assert.Equal(ErrorKind.Permanent, broken.ErrorKind);
        Assert.Equal(
            new[] { "_globals", "app_one", "app_two" },
            result.Items.Where(i => i.SourceId == "pg").Select(i => i.Item).Order().ToArray());
        Assert.All(result.Items.Where(i => i.SourceId != "broken"), i => Assert.Equal(RunStatus.Succeeded, i.Status));

        // Restore the app_one dump and prove it is a valid custom-format archive with our table.
        var snap = Assert.Single(await engine.ListSnapshotsAsync(repo, ["db=app_one"], default));
        var target = _tmp.Combine("restore");
        await engine.RestoreAsync(new RestoreRequest(repo, snap.Id, target, []), default);
        var dumpFile = Path.Combine(target, "app_one.dump");
        Assert.True(File.Exists(dumpFile));

        var listing = new List<string>();
        var list = await restic.Runner.RunAsync(new ProcessSpec
        {
            FileName = Path.Combine(PgBin!, "pg_restore"),
            Arguments = ["--list", dumpFile],
        }, listing.Add, default);
        Assert.Equal(0, list.ExitCode);
        Assert.Contains(listing, l => l.Contains("TABLE public items"));

        var globals = Assert.Single(await engine.ListSnapshotsAsync(repo, ["db=_globals"], default));
        await engine.RestoreAsync(new RestoreRequest(repo, globals.Id, target, []), default);
        Assert.Contains("CREATE ROLE app_user", await File.ReadAllTextAsync(Path.Combine(target, "globals.sql")));

        // Restore test over the same repository: sample verified, every dump listed, the source without
        // snapshots reported as failed, and the work directory removed.
        var tester = new RestoreTester(engine, new StaticPostgresBinLocator([]), restic.Runner, NullLogger<RestoreTester>.Instance);
        var work = _tmp.Combine("restore-test");
        var test = await tester.RunAsync(repo, [policy], sampleFiles: 5, work, default);

        Assert.False(test.Success);
        var file = Assert.Single(test.Items, i => i.SourceId == "files");
        Assert.True(file.Success, file.Error);
        Assert.EndsWith("invoice.txt", file.Item);
        Assert.Equal("No snapshot found for this source", Assert.Single(test.Items, i => i.SourceId == "broken").Error);
        var dumps = test.Items.Where(i => i.SourceId == "pg").ToList();
        Assert.Equal(new[] { "_globals", "app_one", "app_two" }, dumps.Select(i => i.Item).ToArray());
        Assert.All(dumps, d => Assert.True(d.Success, d.Error));
        Assert.False(Directory.Exists(work));
    }

    [SkippableFact]
    public async Task Wrong_postgres_password_fails_source_permanently()
    {
        Skip.If(PgBin is null, "pg_dump not available");

        var pgBuilder = new NpgsqlConnectionStringBuilder(_pg!.GetConnectionString());
        var policy = new PolicySpecDto
        {
            PolicyId = "pol-2",
            Name = "PG only",
            Sources =
            [
                new PostgresSourceDto
                {
                    SourceId = "pg", Host = pgBuilder.Host!, Port = pgBuilder.Port,
                    Username = pgBuilder.Username!, PasswordSecret = "pg-password", BinDirectory = PgBin,
                },
            ],
        };
        var repo = new RepositoryTarget(_tmp.Combine("repo"), "repo-password",
            new Dictionary<string, string> { ["RESTIC_CACHE_DIR"] = _tmp.Combine("cache") });

        var result = await CreateRunner(restic.CreateEngine(), new MemorySecrets { ["pg-password"] = "nope" })
            .RunAsync(policy, repo, "vm-001", default);

        var item = Assert.Single(result.Items);
        Assert.Equal(RunStatus.Failed, item.Status);
        Assert.Equal(ErrorKind.Permanent, item.ErrorKind);
        Assert.Contains("authentication failed", item.Error);
    }

    private PolicyRunner CreateRunner(IBackupEngine engine, ISecretStore secrets) => new(
        engine,
        new PostgresDumpProvider(new StaticPostgresBinLocator([]), restic.Runner, NullLogger<PostgresDumpProvider>.Instance),
        new DirectFileSnapshotProvider(),
        secrets,
        new RetryOptions(MaxRetries: 1, BaseDelay: TimeSpan.FromMilliseconds(100)),
        TimeProvider.System,
        NullLogger<PolicyRunner>.Instance);

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
        if (_s3 is not null) await _s3.DisposeAsync();
        _tmp.Dispose();
    }

    private sealed class MemorySecrets : Dictionary<string, string>, ISecretStore
    {
        public string? Get(string name) => TryGetValue(name, out var v) ? v : null;
        public void Set(string name, string value) => this[name] = value;
    }
}
