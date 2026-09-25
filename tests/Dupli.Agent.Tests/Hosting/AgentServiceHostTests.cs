using Dupli.Agent.Configuration;
using Dupli.Agent.Hosting;
using Dupli.Agent.Secrets;
using Dupli.Agent.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dupli.Agent.Tests.Hosting;

/// <summary>
/// Startup cleanup: only <c>agent-secret</c> survives a restart. Everything else (an old repo password, S3
/// keys, PostgreSQL passwords left over from before just-in-time credentials) is deleted, and a leftover
/// <c>storage-credentials.json</c> from an older agent version is removed too.
/// </summary>
public sealed class AgentServiceHostTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private AgentPaths Paths => new(_tmp.Path);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Removes_every_secret_but_the_agent_secret()
    {
        var paths = Paths;
        paths.EnsureCreated();
        var secrets = new DpapiSecretStore(paths, NullLogger<DpapiSecretStore>.Instance);
        secrets.Set(SecretNames.AgentSecret, "identity");
        secrets.Set("repo-password", "old-repo-password");
        secrets.Set("s3-access-key", "old-access-key");
        secrets.Set("s3-secret-key", "old-secret-key");
        secrets.Set("pg-sample", "old-pg-password");

        AgentServiceHost.CleanupLocalSecrets(paths, secrets);

        Assert.Equal([SecretNames.AgentSecret], secrets.Names());
        Assert.Equal("identity", secrets.Get(SecretNames.AgentSecret));
    }

    [Fact]
    public void Removes_a_leftover_storage_credentials_file()
    {
        var paths = Paths;
        paths.EnsureCreated();
        File.WriteAllText(paths.StorageCredentialsFile, """{"version":2}""");
        var secrets = new DpapiSecretStore(paths, NullLogger<DpapiSecretStore>.Instance);
        secrets.Set(SecretNames.AgentSecret, "identity");

        AgentServiceHost.CleanupLocalSecrets(paths, secrets);

        Assert.False(File.Exists(paths.StorageCredentialsFile));
    }

    [Fact]
    public void Is_a_no_op_when_only_the_agent_secret_is_present()
    {
        var paths = Paths;
        paths.EnsureCreated();
        var secrets = new DpapiSecretStore(paths, NullLogger<DpapiSecretStore>.Instance);
        secrets.Set(SecretNames.AgentSecret, "identity");

        AgentServiceHost.CleanupLocalSecrets(paths, secrets);

        Assert.Equal([SecretNames.AgentSecret], secrets.Names());
    }
}
