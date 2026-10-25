using Tia.Cli.Commands;
using Tia.Core.Infrastructure;

namespace Tia.Integration.Tests;

/// <summary><c>tia hook install|uninstall</c> (#99), in throwaway repositories.</summary>
public sealed class HookTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tia-hook-").FullName;

    public HookTests()
    {
        Git("init", "-q");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string HooksDirectory => Path.Combine(_root, ".git", "hooks");

    [Fact]
    public void A_pre_push_hook_runs_the_selection_against_the_upstream()
    {
        var outcome = GitHook.Install(_root, "pre-push", solution: null);

        Assert.True(outcome.Done, outcome.Message);
        var script = File.ReadAllText(Path.Combine(HooksDirectory, "pre-push"));
        Assert.StartsWith("#!/bin/sh\n" + GitHook.Marker, script, StringComparison.Ordinal);
        Assert.Contains("@{upstream}", script, StringComparison.Ordinal);
        Assert.Contains("exec dotnet tia run --base \"$base\" < /dev/null", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", script, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(outcome.Path!).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void A_pre_commit_hook_runs_against_head_and_names_the_solution()
    {
        var outcome = GitHook.Install(_root, "pre-commit", solution: "src/It's.slnx");

        Assert.True(outcome.Done, outcome.Message);
        var script = File.ReadAllText(Path.Combine(HooksDirectory, "pre-commit"));
        Assert.Contains("base=HEAD\n", script, StringComparison.Ordinal);
        Assert.Contains("--solution 'src/It'\\''s.slnx'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pre-push")]
    [InlineData("pre-commit")]
    public void The_script_is_valid_shell(string kind)
    {
        var path = GitHook.Install(_root, kind, solution: "a b.slnx").Path!;

        // sh ships with git, on Windows too.
        var check = ProcessRunner.Run("sh", ["-n", path], _root, TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(check.Succeeded, check.StandardError);
    }

    [Fact]
    public void Installing_again_updates_its_own_hook()
    {
        GitHook.Install(_root, "pre-push", solution: null);

        var again = GitHook.Install(_root, "pre-push", solution: "App.slnx");

        Assert.True(again.Done);
        Assert.StartsWith("Updated", again.Message, StringComparison.Ordinal);
        Assert.Contains("App.slnx", File.ReadAllText(again.Path!), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hook_it_did_not_write_is_neither_overwritten_nor_removed()
    {
        Directory.CreateDirectory(HooksDirectory);
        var mine = Path.Combine(HooksDirectory, "pre-push");
        File.WriteAllText(mine, "#!/bin/sh\nmake lint\n");

        var install = GitHook.Install(_root, "pre-push", solution: null);
        var uninstall = GitHook.Uninstall(_root, "pre-push");

        Assert.False(install.Done);
        Assert.Contains("add this line", install.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet tia run --base", install.Message, StringComparison.Ordinal);
        Assert.False(uninstall.Done);
        Assert.Equal("#!/bin/sh\nmake lint\n", File.ReadAllText(mine));
    }

    [Fact]
    public void Core_hooks_path_is_where_it_goes()
    {
        Git("config", "core.hooksPath", ".githooks");

        var outcome = GitHook.Install(_root, "pre-push", solution: null);

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, ".githooks", "pre-push")), outcome.Path);
        Assert.False(File.Exists(Path.Combine(HooksDirectory, "pre-push")));
    }

    [Fact]
    public void Uninstall_removes_its_own_hook_and_says_when_there_is_none()
    {
        GitHook.Install(_root, "pre-push", solution: null);

        Assert.True(GitHook.Uninstall(_root, "pre-push").Done);
        Assert.False(File.Exists(Path.Combine(HooksDirectory, "pre-push")));

        var again = GitHook.Uninstall(_root, "pre-push");
        Assert.True(again.Done);
        Assert.StartsWith("No pre-push hook", again.Message, StringComparison.Ordinal);
    }

    private void Git(params string[] arguments)
    {
        var result = ProcessRunner.Run("git", arguments, _root, TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.StandardError);
    }
}
