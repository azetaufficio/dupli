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
/// <see cref="ServerAgentLoop"/> fetches just-in-time credentials for every job but
/// <see cref="RestartAgentJobPayload"/> (a stub <see cref="HttpMessageHandler"/>, same pattern as
/// <c>AgentUpdaterTests</c>) and never writes them to the local secret store: after the job, the store still
/// holds only <c>agent-secret</c>.
/// </summary>
public sealed class ServerJobCredentialsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AgentPaths _paths;
    private readonly MemorySecretStore _secrets = new();

    public ServerJobCredentialsTests()
    {
        _paths = new AgentPaths(_dir.Path);
        _paths.EnsureCreated();
        _secrets.Set(SecretNames.AgentSecret, "agent-secret-value");
    }

    public void Dispose() => _dir.Dispose();

    private AgentConfig Config() => new()
    {
        AgentName = "vm-01",
        Repository = new RepositoryConfig { Endpoint = "http://localhost:9000", Bucket = "backups", Prefix = "agents/vm-01" },
        // A file:// URL to a binary that does not exist: EnsureInstalledAsync fails fast (FileNotFoundException),
        // with no network call, so the job fails deterministically right after fetching its credentials.
        ResticManifest = new ToolManifestDto { Version = "0.19.1", DownloadUrl = "file:///no-such-restic-binary", Sha256 = new string('a', 64) },
        Server = new ServerConfig { Url = "http://server.test", AgentId = Guid.NewGuid().ToString() },
    };

    [Fact]
    public async Task Credentials_are_fetched_once_per_job_and_never_persisted_locally()
    {
        var config = Config();
        var runtime = AgentRuntimeFactory.Build(_paths, config, NullLoggerFactory.Instance, TimeProvider.System, _secrets);
        var ledger = new JobLedger(_paths.LedgerFile);
        var executor = new JobExecutor(runtime, ledger, TimeProvider.System, NullLogger<JobExecutor>.Instance);
        var handler = new StubHandler(JobType.RepositoryCheck, new RepositoryCheckJobPayload { ReadDataSubsetPercent = 0 });
        var client = new ServerClient(new HttpClient(handler), config.Server!, _secrets, TimeProvider.System, NullLogger<ServerClient>.Instance);
        var loop = new ServerAgentLoop(client, ledger, executor, config, _paths, TimeProvider.System,
            new NoopRestarter(), NullLogger<ServerAgentLoop>.Instance);

        await loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, handler.CredentialsRequests);
        Assert.Equal(JobOutcome.Failed, handler.ReportedOutcome);
        Assert.Equal([SecretNames.AgentSecret], _secrets.Names());
        Assert.Equal("agent-secret-value", _secrets.Get(SecretNames.AgentSecret));
    }

    [Fact]
    public async Task RestartAgent_jobs_need_no_credentials()
    {
        var config = Config();
        var runtime = AgentRuntimeFactory.Build(_paths, config, NullLoggerFactory.Instance, TimeProvider.System, _secrets);
        var ledger = new JobLedger(_paths.LedgerFile);
        var executor = new JobExecutor(runtime, ledger, TimeProvider.System, NullLogger<JobExecutor>.Instance);
        var handler = new StubHandler(JobType.RestartAgent, new RestartAgentJobPayload());
        var client = new ServerClient(new HttpClient(handler), config.Server!, _secrets, TimeProvider.System, NullLogger<ServerClient>.Instance);
        var loop = new ServerAgentLoop(client, ledger, executor, config, _paths, TimeProvider.System,
            new NoopRestarter(), NullLogger<ServerAgentLoop>.Instance);

        await loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, handler.CredentialsRequests);
        Assert.Equal(JobOutcome.Succeeded, handler.ReportedOutcome);
        Assert.Equal([SecretNames.AgentSecret], _secrets.Names());
    }

    private sealed class StubHandler(JobType type, JobPayloadDto payload) : HttpMessageHandler
    {
        private readonly string _jobId = Guid.NewGuid().ToString();
        private bool _delivered;

        public int CredentialsRequests { get; private set; }
        public JobOutcome? ReportedOutcome { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/agents/token")
                return Json(new AgentTokenResponse { AccessToken = "token", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) });
            if (path == "/api/agents/heartbeat")
                return Json(new HeartbeatResponse { ServerTime = DateTimeOffset.UtcNow });
            if (path.EndsWith("/jobs", StringComparison.Ordinal))
            {
                if (_delivered)
                    return Json(new List<AgentJobDto>());
                _delivered = true;
                return Json(new List<AgentJobDto>
                {
                    new()
                    {
                        JobId = _jobId,
                        Type = type,
                        Payload = payload,
                        ScheduledAt = DateTimeOffset.UtcNow,
                        LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1),
                    },
                });
            }
            if (path == $"/api/jobs/{_jobId}/started")
                return Json(new JobControlResponse { LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1) });
            if (path == $"/api/agents/jobs/{_jobId}/credentials")
            {
                CredentialsRequests++;
                return Json(new JobCredentialsResponse
                {
                    Repository = new JobRepositoryCredentialsDto { Password = "repo-pw", AccessKeyId = "ak", SecretAccessKey = "sk" },
                });
            }
            if (path == $"/api/jobs/{_jobId}/completed" || path == $"/api/jobs/{_jobId}/failed")
            {
                var result = request.Content!.ReadFromJsonAsync<JobResultDto>(DupliJson.Options, cancellationToken).GetAwaiter().GetResult()!;
                ReportedOutcome = result.Outcome;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
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

        public IReadOnlyList<string> Names() => _values.Keys.Order().ToList();
    }
}
