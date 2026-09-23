using Dupli.Agent.Launcher;

namespace Dupli.Agent.Tests.Launcher;

public sealed class ChildProcessFactoryTests
{
    [SkippableFact]
    public async Task Closing_stdin_stops_a_child_that_waits_for_eof_and_the_exit_code_is_reported()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh");

        using var child = new ChildProcessFactory().Start("/bin/sh", ["-c", "cat > /dev/null; exit \"$CODE\""],
            new Dictionary<string, string> { ["CODE"] = "76" });
        await Task.Delay(100);
        Assert.False(child.Exited.IsCompleted);

        child.RequestStop();

        Assert.Equal(76, await child.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [SkippableFact]
    public async Task Kill_terminates_a_child_that_ignores_stdin()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh");

        using var child = new ChildProcessFactory().Start("/bin/sh", ["-c", "sleep 30"], new Dictionary<string, string>());
        child.RequestStop();
        child.Kill();

        await child.Exited.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
