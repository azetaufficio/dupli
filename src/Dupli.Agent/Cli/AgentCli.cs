using Dupli.Agent.Configuration;
using Dupli.Agent.Hosting;
using Dupli.Agent.Launcher;
using System.CommandLine;

namespace Dupli.Agent.Cli;

/// <summary>Command-line surface of the agent: option/argument definitions and wiring to <see cref="Commands"/>.</summary>
public static class AgentCli
{
    private static readonly Option<string?> HomeOption = new("--home")
    {
        Description = "Overrides DUPLI_HOME (defaults to %ProgramData%\\Dupli).",
        Recursive = true,
    };

    public static RootCommand Build()
    {
        var root = new RootCommand("Dupli agent - file and database backups");
        root.Options.Add(HomeOption);

        root.Subcommands.Add(RunCommand());
        root.Subcommands.Add(LaunchCommand());
        root.Subcommands.Add(VersionCommand());
        root.Subcommands.Add(RotateSecretCommand());
        root.Subcommands.Add(InstallCommand());
        root.Subcommands.Add(UninstallCommand());

        return root;
    }

    private static AgentPaths Paths(ParseResult parseResult) => new(parseResult.GetValue(HomeOption));

    private static Command RunCommand()
    {
        var command = new Command("run", "Runs the agent service loop. The agent must be enrolled with a server: there is no standalone mode.");
        command.SetAction((parseResult, ct) => AgentServiceHost.RunAsync(Paths(parseResult), ct));
        return command;
    }

    private static Command LaunchCommand()
    {
        var command = new Command("launch", "Runs the Launcher (what the service runs): supervises the agent, applies updates, rolls back.");
        command.SetAction((parseResult, ct) => LauncherHost.RunAsync(Paths(parseResult), ct));
        return command;
    }

    private static Command VersionCommand()
    {
        var command = new Command("version", "Prints the agent version.");
        command.SetAction(_ =>
        {
            Console.WriteLine(AgentVersion.Current);
            return 0;
        });
        return command;
    }

    private static Command RotateSecretCommand()
    {
        var command = new Command("rotate-secret", "Rotates the agent secret used to authenticate with the server.");
        command.SetAction((parseResult, ct) => Commands.RotateSecretAsync(Paths(parseResult), ct));
        return command;
    }

    private static Command InstallCommand()
    {
        var server = new Option<string?>("--server") { Description = "Management server URL (with --token)." };
        var token = new Option<string?>("--token") { Description = "Single-use enrollment token. Without it, an enrolled machine is repaired/upgraded in place." };
        var requireSignature = new Option<bool>("--require-signature") { Description = "Accept only Authenticode-signed agent updates." };
        var signer = new Option<string[]>("--signer-thumbprint") { Description = "Accepted signer certificate thumbprint (repeatable)." };

        var command = new Command("install",
            "Enrolls with the server and installs the Launcher service. Without --token: reinstalls binaries, Launcher and service of an enrolled machine.")
        {
            server, token, requireSignature, signer,
        };
        command.Validators.Add(result =>
        {
            if (result.GetValue(token) is not null && result.GetValue(server) is null)
                result.AddError("--token requires --server");
        });
        command.SetAction((parseResult, ct) => Commands.InstallAsync(
            Paths(parseResult),
            parseResult.GetValue(server),
            parseResult.GetValue(token),
            parseResult.GetValue(requireSignature) || (parseResult.GetValue(signer)?.Length ?? 0) > 0
                ? new UpdateConfig { RequireSignature = true, SignerThumbprints = parseResult.GetValue(signer) ?? [] }
                : null,
            ct));
        return command;
    }

    private static Command UninstallCommand()
    {
        var command = new Command("uninstall", "Removes the service and the Launcher. Data in the agent home is kept.");
        command.SetAction((parseResult, ct) => Commands.UninstallAsync(Paths(parseResult), ct));
        return command;
    }
}
