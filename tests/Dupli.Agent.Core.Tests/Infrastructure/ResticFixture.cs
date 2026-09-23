using System.Runtime.InteropServices;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Core.Restic;
using Dupli.Agent.Core.Tools;
using Dupli.Contracts.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Core.Tests.Infrastructure;

/// <summary>Downloads the pinned restic once per test run through the real tool manager.</summary>
public sealed class ResticFixture : IAsyncLifetime
{
    public const string Version = "0.19.1";

    private static readonly Dictionary<string, (string Asset, string Sha256)> Assets = new()
    {
        ["osx-arm64"] = ("restic_0.19.1_darwin_arm64.bz2", "7be0a144ccc377880f294204aa271d76e4b79554b42a751151d425ce6ebac143"),
        ["linux-x64"] = ("restic_0.19.1_linux_amd64.bz2", "f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c"),
        ["win-x64"] = ("restic_0.19.1_windows_amd64.zip", "da948ad707ed690426473aaba2046cd61f8f90f6f0e7dab6be0d5796531de67d"),
    };

    public static string ToolsRoot { get; } =
        Path.Combine(Path.GetTempPath(), "dupli-tests", "tools", "restic");

    public string ResticPath { get; private set; } = "";
    public ProcessRunner Runner { get; } = new(NullLogger<ProcessRunner>.Instance);

    public static ToolManifestDto Manifest()
    {
        var (asset, sha) = Assets[RuntimeInformation.RuntimeIdentifier];
        return new ToolManifestDto
        {
            Version = Version,
            DownloadUrl = $"https://github.com/restic/restic/releases/download/v{Version}/{asset}",
            Sha256 = sha,
        };
    }

    public ResticBackupEngine CreateEngine() =>
        new(new FixedBinary(ResticPath), Runner, NullLogger<ResticBackupEngine>.Instance);

    public async Task InitializeAsync()
    {
        var manager = new ResticToolManager(ToolsRoot, new HttpClient(), Runner, NullLogger<ResticToolManager>.Instance);
        ResticPath = await manager.EnsureInstalledAsync(Manifest(), CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FixedBinary(string path) : IResticBinaryProvider
    {
        public Task<string> GetPathAsync(CancellationToken cancellationToken) => Task.FromResult(path);
    }
}

[CollectionDefinition(Name)]
public sealed class ResticCollection : ICollectionFixture<ResticFixture>
{
    public const string Name = "restic";
}

/// <summary>Temporary directory deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "dupli-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
