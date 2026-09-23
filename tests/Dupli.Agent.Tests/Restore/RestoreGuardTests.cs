using Dupli.Agent.Configuration;
using Dupli.Agent.Restore;
using Dupli.Agent.Tests.Infrastructure;
using Dupli.Contracts.Policies;

namespace Dupli.Agent.Tests.Restore;

public sealed class RestoreGuardTests : IDisposable
{
    private readonly TempDir _tmp = new();

    private IReadOnlyList<PolicySpecDto> Policies(params string[] sourcePaths) =>
    [
        new PolicySpecDto
        {
            PolicyId = "p1",
            Name = "Policy",
            Sources =
            [
                new DirectorySourceDto { SourceId = "s1", Paths = sourcePaths },
            ],
        },
    ];

    [Fact]
    public void Rejects_target_identical_to_a_source_path()
    {
        var source = _tmp.Combine("data");
        Directory.CreateDirectory(source);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RestoreGuard.ResolveTarget(source, "snap1", new AgentPaths(_tmp.Path), Policies(source)));
        Assert.Contains("coincides", ex.Message);
    }

    [Fact]
    public void Rejects_target_nested_inside_a_source_path()
    {
        var source = _tmp.Combine("data");
        var nestedTarget = Path.Combine(source, "sub", "restore");
        Directory.CreateDirectory(source);

        Assert.Throws<InvalidOperationException>(() =>
            RestoreGuard.ResolveTarget(nestedTarget, "snap1", new AgentPaths(_tmp.Path), Policies(source)));
    }

    [Fact]
    public void Rejects_source_path_nested_inside_target()
    {
        var target = _tmp.Combine("restore-root");
        var source = Path.Combine(target, "data");
        Directory.CreateDirectory(target);

        Assert.Throws<InvalidOperationException>(() =>
            RestoreGuard.ResolveTarget(target, "snap1", new AgentPaths(_tmp.Path), Policies(source)));
    }

    [Fact]
    public void Accepts_an_unrelated_target()
    {
        var source = _tmp.Combine("data");
        var target = _tmp.Combine("restore", "snap1");
        Directory.CreateDirectory(source);

        var resolved = RestoreGuard.ResolveTarget(target, "snap1", new AgentPaths(_tmp.Path), Policies(source));

        Assert.Equal(Path.GetFullPath(target), resolved);
    }

    [Fact]
    public void Defaults_to_a_path_that_does_not_collide_with_any_source()
    {
        var source = _tmp.Combine("data");
        Directory.CreateDirectory(source);

        var resolved = RestoreGuard.ResolveTarget(null, "snap1", new AgentPaths(_tmp.Path), Policies(source));

        Assert.Contains("snap1", resolved);
        Assert.DoesNotContain(source, resolved, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _tmp.Dispose();
}
