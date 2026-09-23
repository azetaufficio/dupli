using System.Net;
using System.Security.Cryptography;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Launcher;
using Dupli.Agent.Tests.Infrastructure;
using Dupli.Agent.Updates;
using Dupli.Contracts.Agents;
using Dupli.Contracts.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Tests.Updates;

public sealed class AgentUpdaterTests : IDisposable
{
    private const string NewVersion = "9.9.9";
    private static readonly byte[] Package = "new agent build"u8.ToArray();

    private readonly TempDir _dir = new();
    private readonly AgentPaths _paths;
    private readonly VersionFiles _files;
    private readonly FakeRunner _runner = new();
    private readonly StubHandler _handler = new();

    public AgentUpdaterTests()
    {
        _paths = new AgentPaths(_dir.Path);
        _paths.EnsureCreated();
        _files = new VersionFiles(_paths);
    }

    public void Dispose() => _dir.Dispose();

    private AgentUpdater CreateUpdater(bool launcherManaged = true, IPackageSignatureVerifier? signature = null) => new(
        _files, _paths, new HttpClient(_handler), _runner, signature ?? new AuthenticodeVerifier(new UpdateConfig()),
        TimeProvider.System, NullLogger<AgentUpdater>.Instance)
    {
        LauncherManagedOverride = launcherManaged,
    };

    private static ToolManifestDto Manifest(string version = NewVersion, byte[]? content = null) => new()
    {
        Version = version,
        DownloadUrl = $"https://server.test/api/tools/agent/{version}/linux_amd64",
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content ?? Package)),
    };

    [Fact]
    public async Task Stages_verified_package_and_writes_pending()
    {
        _runner.Output = NewVersion;

        Assert.True(await CreateUpdater().TryStageAsync(Manifest(), CancellationToken.None));

        var pending = _files.ReadPending()!;
        Assert.Equal(NewVersion, pending.Version);
        Assert.Equal(Path.Combine(_files.VersionDirectory(NewVersion), VersionFiles.ExeFileName), pending.ExePath);
        Assert.Equal(Package, File.ReadAllBytes(pending.ExePath));
        Assert.Equal(VersionFiles.Sha256Of(pending.ExePath), pending.Sha256);
        Assert.Equal(["version"], _runner.LastArguments);
        Assert.Empty(Directory.GetDirectories(_paths.Versions, ".*"));
    }

    [Fact]
    public async Task Sha_mismatch_is_rejected_without_staging()
    {
        _runner.Output = NewVersion;

        Assert.False(await CreateUpdater().TryStageAsync(Manifest(content: "other"u8.ToArray()), CancellationToken.None));

        Assert.Null(_files.ReadPending());
        Assert.False(Directory.Exists(_files.VersionDirectory(NewVersion)));
        Assert.Empty(Directory.GetDirectories(_paths.Versions));
    }

    [Fact]
    public async Task Package_reporting_another_version_is_rejected()
    {
        _runner.Output = "1.2.3";

        Assert.False(await CreateUpdater().TryStageAsync(Manifest(), CancellationToken.None));
        Assert.Null(_files.ReadPending());
    }

    [Fact]
    public async Task Failed_signature_is_rejected()
    {
        _runner.Output = NewVersion;

        Assert.False(await CreateUpdater(signature: new RejectingVerifier()).TryStageAsync(Manifest(), CancellationToken.None));
        Assert.Null(_files.ReadPending());
    }

    [Fact]
    public async Task Rolled_back_version_is_not_retried()
    {
        _files.WriteUpdateState(new UpdateStateFile { FailedVersions = [NewVersion] });

        Assert.False(await CreateUpdater().TryStageAsync(Manifest(), CancellationToken.None));
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task Not_launched_by_the_launcher_or_same_version_does_nothing()
    {
        Assert.False(await CreateUpdater(launcherManaged: false).TryStageAsync(Manifest(), CancellationToken.None));
        Assert.False(await CreateUpdater().TryStageAsync(Manifest(AgentVersion.Current), CancellationToken.None));
        Assert.False(await CreateUpdater().TryStageAsync(null, CancellationToken.None));
        Assert.False(await CreateUpdater().TryStageAsync(Manifest("../evil"), CancellationToken.None));
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task Download_failure_backs_off()
    {
        _handler.Status = HttpStatusCode.ServiceUnavailable;
        var updater = CreateUpdater();

        Assert.False(await updater.TryStageAsync(Manifest(), CancellationToken.None));
        Assert.False(await updater.TryStageAsync(Manifest(), CancellationToken.None));
        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public void Version_strips_build_metadata()
    {
        Assert.False(AgentVersion.Current.Contains('+'));
        Assert.True(AgentVersion.IsValid("0.2.0-beta.1"));
        Assert.False(AgentVersion.IsValid("1.0.0/../x"));
        Assert.False(AgentVersion.IsValid("v1.0.0"));
    }

    private sealed class RejectingVerifier : IPackageSignatureVerifier
    {
        public void Verify(string file) => throw new InvalidDataException("unsigned");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Package) });
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public string Output { get; set; } = "";
        public IReadOnlyList<string> LastArguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onStdoutLine, CancellationToken cancellationToken)
        {
            LastArguments = spec.Arguments.ToList();
            onStdoutLine?.Invoke(Output);
            return Task.FromResult(new ProcessResult(0, []));
        }
    }
}
