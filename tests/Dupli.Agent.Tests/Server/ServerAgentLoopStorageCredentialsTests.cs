using System.Net;
using System.Net.Http.Json;
using Dupli.Agent.Cli;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Secrets;
using Dupli.Agent.Server;
using Dupli.Agent.Tests.Infrastructure;
using Dupli.Contracts;
using Dupli.Contracts.Agents;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Tests.Server;

/// <summary>
/// <see cref="ServerAgentLoop.RunOnceAsync"/> against a fake server (an <see cref="HttpMessageHandler"/> stub,
/// same pattern as <c>AgentUpdaterTests</c>): a heartbeat reporting a higher desired storage credentials
/// version makes the agent fetch and apply the new S3 key, and persist the version it applied.
/// </summary>
public sealed class ServerAgentLoopStorageCredentialsTests : IDisposable
{
    private const string NewAccessKey = "new-access-key";
    private const string NewSecretKey = "new-secret-key";

    private readonly TempDir _dir = new();
    private readonly AgentPaths _paths;
    private readonly MemorySecretStore _secrets = new();
    private readonly StubHandler _handler = new();

    public ServerAgentLoopStorageCredentialsTests()
    {
        _paths = new AgentPaths(_dir.Path);
        _paths.EnsureCreated();
        _secrets.Set(SecretNames.AgentSecret, "agent-secret");
        _secrets.Set(SecretNames.RepositoryPassword, "repo-password");
        _secrets.Set(SecretNames.S3AccessKey, "old-access-key");
        _secrets.Set(SecretNames.S3SecretKey, "old-secret-key");
    }

    public void Dispose() => _dir.Dispose();

    private AgentConfig Config() => new()
    {
        AgentName = "vm-01",
        Repository = new RepositoryConfig
        {
            Endpoint = "http://localhost:9000",
            Bucket = "backups",
            Prefix = "agents/vm-01",
            PasswordSecret = SecretNames.RepositoryPassword,
            AccessKeySecret = SecretNames.S3AccessKey,
            SecretKeySecret = SecretNames.S3SecretKey,
        },
        ResticManifest = new ToolManifestDto { Version = "0.19.1", DownloadUrl = "https://example.test/restic", Sha256 = new string('a', 64) },
        Server = new ServerConfig { Url = "http://server.test", AgentId = Guid.NewGuid().ToString() },
    };

    [Fact]
    public async Task Applies_the_storage_credentials_version_the_heartbeat_desires()
    {
        var config = Config();
        var runtime = AgentRuntimeFactory.Build(_paths, config, NullLoggerFactory.Instance, TimeProvider.System, _secrets);
        var ledger = new JobLedger(_paths.LedgerFile);
        var executor = new JobExecutor(runtime, ledger, TimeProvider.System, NullLogger<JobExecutor>.Instance);
        var client = new ServerClient(new HttpClient(_handler), config.Server!, _secrets, TimeProvider.System, NullLogger<ServerClient>.Instance);
        var loop = new ServerAgentLoop(client, ledger, executor, config, _paths, _secrets, TimeProvider.System,
            new NoopRestarter(), NullLogger<ServerAgentLoop>.Instance);

        Assert.Equal(1, StorageCredentialsStateFile.Read(_paths)); // no file yet: assumed version 1

        await loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(NewAccessKey, _secrets.Get(SecretNames.S3AccessKey));
        Assert.Equal(NewSecretKey, _secrets.Get(SecretNames.S3SecretKey));
        Assert.Equal(2, StorageCredentialsStateFile.Read(_paths));
        Assert.Equal(1, _handler.CredentialsRequests); // fetched once, not on every poll

        // The next heartbeat reports the version now applied: nothing more to fetch.
        await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, _handler.CredentialsRequests);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int CredentialsRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/agents/token")
                return Json(new AgentTokenResponse { AccessToken = "token", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) });
            if (path == "/api/agents/heartbeat")
                return Json(new HeartbeatResponse { ServerTime = DateTimeOffset.UtcNow, StorageCredentialsVersion = 2 });
            if (path == "/api/agents/storage-credentials")
            {
                CredentialsRequests++;
                return Json(new StorageCredentialsResponse { Version = 2, AccessKeyId = NewAccessKey, SecretAccessKey = NewSecretKey });
            }
            if (path.EndsWith("/jobs", StringComparison.Ordinal))
                return Json(new List<AgentJobDto>());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json<T>(T value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value, options: DupliJson.Options),
        });
    }

    private sealed class NoopRestarter : IAgentRestarter
    {
        public void Restart()
        {
        }

        public void ExitForUpdate()
        {
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Get(string name) => _values.GetValueOrDefault(name);

        public void Set(string name, string value) => _values[name] = value;
    }
}
