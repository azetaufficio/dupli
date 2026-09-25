using Dupli.Agent.Configuration;
using Dupli.Agent.Install;
using Dupli.Agent.Logging;
using Dupli.Agent.Server;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Dupli.Agent.Cli;

/// <summary>One-shot CLI command implementations. Each loads config, does its job, returns an exit code.</summary>
public static class Commands
{
    /// <summary>Rotates the AgentSecret. The new value is stored before anything else can fail.</summary>
    public static async Task<int> RotateSecretAsync(AgentPaths paths, CancellationToken ct)
    {
        var config = AgentConfigLoader.Load(paths.ConfigFile);
        var server = config.Server ?? throw new InvalidOperationException("The agent is not enrolled with a server");
        using var logger = CreateLoggerFactory(paths, server.AgentId);
        var secrets = new Secrets.DpapiSecretStore(paths, logger.CreateLogger<Secrets.DpapiSecretStore>());

        var client = new ServerClient(new HttpClient(), server, secrets, TimeProvider.System, logger.CreateLogger<ServerClient>());
        var rotated = await client.RotateSecretAsync(ct);
        secrets.Set(server.AgentSecretName, rotated.AgentSecret);
        Console.WriteLine("Agent secret rotated.");
        return 0;
    }

    public static async Task<int> InstallAsync(AgentPaths paths, string? server, string? token, UpdateConfig? update, CancellationToken ct)
    {
        using var logger = LoggerFactory.Create(b => b.AddConsole());
        return await AgentInstaller.InstallAsync(paths, server, token, update, logger.CreateLogger("install"), ct);
    }

    public static async Task<int> UninstallAsync(AgentPaths paths, CancellationToken ct)
    {
        using var logger = LoggerFactory.Create(b => b.AddConsole());
        return await AgentInstaller.UninstallAsync(paths, logger.CreateLogger("uninstall"), ct);
    }

    private static ILoggerFactory CreateLoggerFactory(AgentPaths paths, string agentName)
    {
        var serilogLogger = SerilogSetup.CreateLogger(paths, agentName);
        return LoggerFactory.Create(b => b.AddSerilog(serilogLogger, dispose: true));
    }
}
