using Dupli.Agent.Configuration;
using Dupli.Agent.Secrets;
using Dupli.Agent.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Tests.Secrets;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    private DpapiSecretStore Store() => new(new AgentPaths(_tmp.Path), NullLogger<DpapiSecretStore>.Instance);

    [SkippableFact]
    public void Windows_roundtrips_a_secret_through_dpapi()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is only available on Windows");

        var store = Store();
        store.Set("my-secret", "s3kr3t-value");

        Assert.Equal("s3kr3t-value", store.Get("my-secret"));
        Assert.True(File.Exists(Path.Combine(_tmp.Path, "config", "secrets", "my-secret.bin")));
    }

    [SkippableFact]
    public void Windows_missing_secret_returns_null()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is only available on Windows");

        Assert.Null(Store().Get("does-not-exist"));
    }

    [SkippableFact]
    public void NonWindows_set_is_rejected()
    {
        Skip.If(OperatingSystem.IsWindows(), "exercises the non-Windows fallback only");

        Assert.Throws<PlatformNotSupportedException>(() => Store().Set("my-secret", "value"));
    }

    [SkippableFact]
    public void NonWindows_get_falls_back_to_environment_variable()
    {
        Skip.If(OperatingSystem.IsWindows(), "exercises the non-Windows fallback only");

        Environment.SetEnvironmentVariable("DUPLI_SECRET_DEV_ONLY", "from-env");
        try
        {
            Assert.Equal("from-env", Store().Get("dev_only"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUPLI_SECRET_DEV_ONLY", null);
        }
    }

    [Fact]
    public void Rejects_invalid_secret_names()
    {
        Assert.Throws<ArgumentException>(() => Store().Get("bad name!"));
    }

    public void Dispose() => _tmp.Dispose();
}
