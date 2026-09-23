using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Backup;
using Dupli.Agent.Core.Postgres;
using Dupli.Agent.Core.Processes;
using Dupli.Agent.Core.Restic;
using Dupli.Agent.Core.Secrets;
using Dupli.Agent.Core.Snapshots;
using Dupli.Agent.Core.Tools;
using Dupli.Agent.Core.Verification;
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
    PolicyRunner PolicyRunner,
    RestoreTester RestoreTester,
    ActiveResticManifest Restic,
    ResticToolManager ResticTools,
    IProcessRunner Processes);

public static class AgentRuntimeFactory
{
    public static AgentRuntime Build(
        AgentPaths paths, AgentConfig config, ILoggerFactory loggerFactory, TimeProvider? time = null, ISecretStore? secretStore = null)
    {
        paths.EnsureCreated();

        var secrets = secretStore ?? new DpapiSecretStore(paths, loggerFactory.CreateLogger<DpapiSecretStore>());
        var repository = config.Repository.Resolve(secrets, config.ResticCacheDir ?? paths.Cache);

        var processRunner = new ProcessRunner(loggerFactory.CreateLogger<ProcessRunner>());
        var toolManager = new ResticToolManager(
            paths.ResticTools, new HttpClient(), processRunner, loggerFactory.CreateLogger<ResticToolManager>());
        // A restic update (config\restic.json) overrides the release received at enrollment.
        var restic = new ActiveResticManifest(Updates.ResticUpdater.LoadPersisted(paths) ?? config.ResticManifest);
        var binaryProvider = new ManagedResticBinaryProvider(toolManager, restic);
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

        var restoreTester = new RestoreTester(engine, binLocator, processRunner, loggerFactory.CreateLogger<RestoreTester>());

        return new AgentRuntime(config, paths, secrets, engine, repository, policyRunner, restoreTester, restic, toolManager, processRunner);
    }
}
