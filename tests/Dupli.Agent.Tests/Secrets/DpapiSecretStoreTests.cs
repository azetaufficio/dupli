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
    public void NonWindows_secret_roundtrips_through_an_owner_only_file()
    {
        Skip.If(OperatingSystem.IsWindows(), "exercises the non-Windows fallback only");
        if (OperatingSystem.IsWindows())
            return;

        Store().Set("my-secret", "value");

        Assert.Equal("value", Store().Get("my-secret"));
        var file = Path.Combine(_tmp.Path, "config", "secrets", "my-secret.secret");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(file)!));
    }

    /// <summary>The env var override is honored only for the agent secret: every business credential now
    /// comes from a per-job fetch, never from an environment variable.</summary>
    [SkippableFact]
    public void NonWindows_get_falls_back_to_environment_variable_for_the_agent_secret_only()
    {
        Skip.If(OperatingSystem.IsWindows(), "exercises the non-Windows fallback only");

        Environment.SetEnvironmentVariable("DUPLI_SECRET_AGENT-SECRET", "from-env");
        try
        {
            Assert.Equal("from-env", Store().Get(SecretNames.AgentSecret));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUPLI_SECRET_AGENT-SECRET", null);
        }
    }

    [SkippableFact]
    public void NonWindows_get_ignores_the_environment_variable_for_any_other_secret()
    {
        Skip.If(OperatingSystem.IsWindows(), "exercises the non-Windows fallback only");

        Environment.SetEnvironmentVariable("DUPLI_SECRET_OTHER", "from-env");
        try
        {
            Assert.Null(Store().Get("other"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUPLI_SECRET_OTHER", null);
        }
    }

    [Fact]
    public void Rejects_invalid_secret_names()
    {
        Assert.Throws<ArgumentException>(() => Store().Get("bad name!"));
    }

    [Fact]
    public void Names_lists_every_stored_secret_and_delete_removes_one()
    {
        var store = Store();
        store.Set("agent-secret", "a");
        store.Set("repo-password", "b");

        Assert.Equal(["agent-secret", "repo-password"], store.Names().Order());

        store.Delete("repo-password");

        Assert.Equal(["agent-secret"], store.Names());
        Assert.Null(store.Get("repo-password"));
    }

    [Fact]
    public void Names_is_empty_when_nothing_was_ever_stored()
    {
        Assert.Empty(Store().Names());
    }

    public void Dispose() => _tmp.Dispose();
}
