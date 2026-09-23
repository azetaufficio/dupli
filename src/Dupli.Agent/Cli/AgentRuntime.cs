using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Postgres;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Core.Restic;
using Dupli.Agent.Core.Secrets;
using Dupli.Agent.Core.Snapshots;
using Dupli.Agent.Core.Tools;
using Dupli.Agent.Secrets;
using Microsoft.Extensions.Logging;

namespace Dupli.Agent.Cli;

/// <summary>Everything a CLI command or the scheduler needs to run a policy, wired once per invocation.</summary>
public sealed record AgentRuntime(
    AgentConfig Config,
    AgentPaths Paths,
    ISecretStore Secrets,
    IBackupEngine Engine,
    RepositoryTarget Repository,
    PolicyRunner PolicyRunner);

public static class AgentRuntimeFactory
{
    public static AgentRuntime Build(AgentPaths paths, AgentConfig config, ILoggerFactory loggerFactory, TimeProvider? time = null)
    {
        paths.EnsureCreated();

        ISecretStore secrets = new DpapiSecretStore(paths, loggerFactory.CreateLogger<DpapiSecretStore>());
        var repository = config.Repository.Resolve(secrets, config.ResticCacheDir ?? paths.Cache);

        var processRunner = new ProcessRunner(loggerFactory.CreateLogger<ProcessRunner>());
        var toolManager = new ResticToolManager(
            paths.ResticTools, new HttpClient(), processRunner, loggerFactory.CreateLogger<ResticToolManager>());
        var binaryProvider = new ManagedResticBinaryProvider(toolManager, config.ResticManifest);
        IBackupEngine engine = new ResticBackupEngine(binaryProvider, processRunner, loggerFactory.CreateLogger<ResticBackupEngine>());

        IPostgresBinLocator binLocator = OperatingSystem.IsWindows()
            ? new Postgres.WindowsRegistryPostgresBinLocator(loggerFactory.CreateLogger<Postgres.WindowsRegistryPostgresBinLocator>())
            : new StaticPostgresBinLocator([]);
        IDatabaseBackupProvider databases = new PostgresDumpProvider(
            binLocator, processRunner, loggerFactory.CreateLogger<PostgresDumpProvider>());

        IFileSnapshotProvider snapshots = new DirectFileSnapshotProvider();
        var retry = new RetryOptions(config.Retry.MaxRetries, TimeSpan.FromSeconds(config.Retry.BaseDelaySeconds));
        var policyRunner = new PolicyRunner(
            engine, databases, snapshots, secrets, retry, time ?? TimeProvider.System, loggerFactory.CreateLogger<PolicyRunner>());

        return new AgentRuntime(config, paths, secrets, engine, repository, policyRunner);
    }
}
