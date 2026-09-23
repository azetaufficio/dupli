using Dupli.Agent.Core.Errors;
using Dupli.Agent.Core.Tests.Infrastructure;
using Dupli.Agent.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Core.Tests;

[Collection(ResticCollection.Name)]
public sealed class ResticToolManagerTests(ResticFixture restic) : IDisposable
{
    private readonly TempDir _tmp = new();

    private ResticToolManager Manager() =>
        new(_tmp.Path, new HttpClient(), restic.Runner, NullLogger<ResticToolManager>.Instance);

    [Fact]
    public void Installs_under_versioned_directory()
    {
        Assert.Equal(
            Path.Combine(ResticFixture.ToolsRoot, ResticFixture.Version, OperatingSystem.IsWindows() ? "restic.exe" : "restic"),
            restic.ResticPath);
        Assert.True(File.Exists(restic.ResticPath));
    }

    [Fact]
    public async Task Checksum_mismatch_is_rejected_and_nothing_is_installed()
    {
        var manifest = ResticFixture.Manifest() with { Sha256 = new string('0', 64) };

        var ex = await Assert.ThrowsAsync<BackupException>(() => Manager().EnsureInstalledAsync(manifest, default));

        Assert.Equal(ErrorKind.Permanent, ex.Kind);
        Assert.Contains("SHA256 mismatch", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_tmp.Path));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("../evil")]
    [InlineData("")]
    public async Task Unpinned_or_invalid_versions_are_rejected(string version)
    {
        var manifest = ResticFixture.Manifest() with { Version = version };
        await Assert.ThrowsAsync<ArgumentException>(() => Manager().EnsureInstalledAsync(manifest, default));
    }

    public void Dispose() => _tmp.Dispose();
}
