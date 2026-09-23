using Dupli.Agent.Configuration;
using Dupli.Agent.Hosting;
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
        var root = new RootCommand("Dupli agent - file and PostgreSQL backups via restic");
        root.Options.Add(HomeOption);

        root.Subcommands.Add(RunCommand());
        root.Subcommands.Add(BackupCommand());
        root.Subcommands.Add(SnapshotsCommand());
        root.Subcommands.Add(RestoreCommand());
        root.Subcommands.Add(ForgetCommand());
        root.Subcommands.Add(CheckCommand());
        root.Subcommands.Add(SecretCommand());
        root.Subcommands.Add(InstallCommand());
        root.Subcommands.Add(UninstallCommand());

        return root;
    }

    private static AgentPaths Paths(ParseResult parseResult) => new(parseResult.GetValue(HomeOption));

    private static Option<string> PolicyOption() => new("--policy") { Description = "Policy name or id.", Required = true };

    private static Command RunCommand()
    {
        var command = new Command("run", "Runs the agent service loop (scheduler for local policies).");
        command.SetAction((parseResult, ct) => AgentServiceHost.RunAsync(Paths(parseResult), ct));
        return command;
    }

    private static Command BackupCommand()
    {
        var policy = PolicyOption();

        var command = new Command("backup", "Runs a single policy once.") { policy };
        command.SetAction((parseResult, ct) =>
            Commands.BackupAsync(Paths(parseResult), parseResult.GetRequiredValue(policy), ct));
        return command;
    }

    private static Command SnapshotsCommand()
    {
        var tag = new Option<string[]>("--tag") { Description = "Filter by tag (repeatable)." };

        var command = new Command("snapshots", "Lists snapshots in the repository.") { tag };
        command.SetAction((parseResult, ct) =>
            Commands.SnapshotsAsync(Paths(parseResult), parseResult.GetValue(tag) ?? [], ct));
        return command;
    }

    private static Command RestoreCommand()
    {
        var snapshot = new Option<string>("--snapshot") { Description = "Snapshot id.", Required = true };
        var target = new Option<string?>("--target") { Description = "Restore target directory (defaults to C:\\DupliRestore\\<snapshot-id>)." };
        var include = new Option<string[]>("--include") { Description = "Restrict restore to this path (repeatable)." };

        var command = new Command("restore", "Restores a snapshot to an alternative location. Never overwrites a configured source path.")
        {
            snapshot, target, include,
        };
        command.SetAction((parseResult, ct) => Commands.RestoreAsync(
            Paths(parseResult),
            parseResult.GetRequiredValue(snapshot),
            parseResult.GetValue(target),
            parseResult.GetValue(include) ?? [],
            ct));
        return command;
    }

    private static Command ForgetCommand()
    {
        var policy = PolicyOption();
        var prune = new Option<bool>("--prune") { Description = "Also prune unreferenced data (slower, frees space)." };

        var command = new Command("forget", "Applies retention for a policy.") { policy, prune };
        command.SetAction((parseResult, ct) => Commands.ForgetAsync(
            Paths(parseResult), parseResult.GetRequiredValue(policy), parseResult.GetValue(prune), ct));
        return command;
    }

    private static Command CheckCommand()
    {
        var subset = new Option<int>("--subset")
        {
            Description = "Percentage of data to read-verify (0 = structure only).",
            DefaultValueFactory = _ => 0,
        };

        var command = new Command("check", "Verifies repository integrity.") { subset };
        command.SetAction((parseResult, ct) =>
            Commands.CheckAsync(Paths(parseResult), parseResult.GetValue(subset), ct));
        return command;
    }

    private static Command SecretCommand()
    {
        var name = new Argument<string>("name") { Description = "Secret name. Value is read from stdin, never from an argument." };

        var set = new Command("set", "Stores a secret (value read from stdin).") { name };
        set.SetAction((parseResult, ct) =>
            Commands.SecretSetAsync(Paths(parseResult), parseResult.GetRequiredValue(name), ct));

        return new Command("secret", "Manages local secrets (DPAPI-protected).") { set };
    }

    private static Command InstallCommand()
    {
        var server = new Option<string>("--server") { Description = "Management server URL.", Required = true };
        var token = new Option<string>("--token") { Description = "Single-use enrollment token.", Required = true };

        var command = new Command("install", "Installs the agent as a Windows service (M1: local install only).") { server, token };
        command.SetAction((parseResult, ct) => Commands.InstallAsync(
            Paths(parseResult), parseResult.GetRequiredValue(server), parseResult.GetRequiredValue(token), ct));
        return command;
    }

    private static Command UninstallCommand()
    {
        var command = new Command("uninstall", "Removes the Windows service.");
        command.SetAction((_, ct) => Commands.UninstallAsync(ct));
        return command;
    }
}
