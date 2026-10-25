using System.CommandLine;
using System.Text;
using Tia.Core.Infrastructure;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>What <see cref="GitHook"/> did, and the sentence that says so.</summary>
public sealed record HookOutcome(bool Done, string Message, string? Path = null);

/// <summary>
/// A git hook that runs the selected tests before a push or a commit (#99).
/// </summary>
/// <remarks>
/// <para>
/// The cheapest place to run selected tests is before the code leaves the machine. Setting that up
/// meant writing a hook by hand and getting <c>--base</c> right for the branch. The hook works the
/// base out each time it runs: the branch's upstream, so a push tests what it is about to send, or
/// the remote's default branch for a branch that has never been pushed.
/// </para>
/// <para>
/// A hook this tool did not write is never overwritten or removed. One line marks the file as its
/// own, and a hook without it is someone's work.
/// </para>
/// </remarks>
public static class GitHook
{
    public const string Marker = "# dotnet-tia hook";

    public static readonly string[] Kinds = ["pre-push", "pre-commit"];

    /// <summary>
    /// Where git looks for hooks: <c>core.hooksPath</c> when set, the repository's own hooks
    /// directory otherwise, and the shared one from a linked worktree. Asked of git, because it
    /// is git's answer that decides whether the hook runs.
    /// </summary>
    public static string? HooksDirectory(string repositoryRoot)
    {
        var result = ProcessRunner.Run("git", ["rev-parse", "--git-path", "hooks"], repositoryRoot, TimeSpan.FromSeconds(30));
        var path = result.StandardOutput.Trim();
        return result.Succeeded && path.Length > 0 ? Path.GetFullPath(Path.Combine(repositoryRoot, path)) : null;
    }

    public static string Script(string kind, string? solution)
    {
        var script = new StringBuilder();
        script.Append("#!/bin/sh\n");
        script.Append(Marker).Append(": written by `dotnet tia hook install`, removed by `dotnet tia hook uninstall`.\n");
        script.Append("# Runs the tests the changes since the base select, and stops the ")
            .Append(kind == "pre-commit" ? "commit" : "push")
            .Append(" if one fails.\n");
        script.Append("# Skip it once with `git ").Append(kind == "pre-commit" ? "commit" : "push").Append(" --no-verify`. A local convenience; CI still decides.\n");
        script.Append('\n');

        if (kind == "pre-commit")
        {
            script.Append("base=HEAD\n");
        }
        else
        {
            // The upstream is what the push is measured against; a branch never pushed has none,
            // and is measured against the remote's default branch instead.
            script.Append("base=$(git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null)\n");
            script.Append("[ -n \"$base\" ] || base=$(git symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>/dev/null)\n");
            script.Append("[ -n \"$base\" ] || base=origin/main\n");
        }

        script.Append('\n');
        script.Append("echo \"dotnet-tia: running the tests selected against $base\" >&2\n");
        script.Append("exec dotnet tia run --base \"$base\"");
        if (solution is { Length: > 0 })
        {
            script.Append(" --solution ").Append(Quote(solution));
        }

        // A hook's stdin is git's list of refs being pushed, and nothing in a test run should read it.
        script.Append(" < /dev/null\n");
        return script.ToString();
    }

    public static HookOutcome Install(string repositoryRoot, string kind, string? solution)
    {
        if (HooksDirectory(repositoryRoot) is not { } directory)
        {
            return new HookOutcome(false, "Not a git repository, or git could not say where its hooks go.");
        }

        var path = Path.Combine(directory, kind);
        var updating = File.Exists(path);
        if (updating && !IsOurs(path))
        {
            return new HookOutcome(false,
                $"A {kind} hook that dotnet-tia did not write is already there: {path}. It is left alone. " +
                $"To run the selected tests from it, add this line to it:{Environment.NewLine}{Environment.NewLine}" +
                $"    dotnet tia run --base {(kind == "pre-commit" ? "HEAD" : "\"$(git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}')\"")}",
                path);
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, Script(kind, solution), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return new HookOutcome(true, $"{(updating ? "Updated" : "Installed")} the {kind} hook: {path}", path);
    }

    public static HookOutcome Uninstall(string repositoryRoot, string kind)
    {
        if (HooksDirectory(repositoryRoot) is not { } directory)
        {
            return new HookOutcome(false, "Not a git repository, or git could not say where its hooks go.");
        }

        var path = Path.Combine(directory, kind);
        if (!File.Exists(path))
        {
            return new HookOutcome(true, $"No {kind} hook is installed.", path);
        }

        if (!IsOurs(path))
        {
            return new HookOutcome(false, $"The {kind} hook at {path} was not written by dotnet-tia, so it is left alone.", path);
        }

        File.Delete(path);
        return new HookOutcome(true, $"Removed the {kind} hook: {path}", path);
    }

    private static bool IsOurs(string path)
    {
        try
        {
            return File.ReadLines(path).Take(5).Any(l => l.StartsWith(Marker, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One shell word, whatever it holds.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary><c>tia hook install|uninstall</c> (#99).</summary>
public static class HookCommand
{
    public static Command Create(CommonOptions common)
    {
        var prePush = new Option<bool>("--pre-push") { Description = "Run before every push, against the branch's upstream. The default." };
        var preCommit = new Option<bool>("--pre-commit") { Description = "Run before every commit, against HEAD." };

        var hook = new Command("hook", "Run the selected tests from a git hook, before a push or a commit.");

        var install = new Command("install", "Write a git hook that runs `dotnet tia run` against the right base.");
        install.Options.Add(common.Path);
        install.Options.Add(common.Solution);
        install.Options.Add(prePush);
        install.Options.Add(preCommit);
        install.SetAction(parseResult =>
        {
            if (Kind(parseResult, prePush, preCommit) is not { } kind)
            {
                return ExitCodes.Inconclusive;
            }

            var options = common.Read(parseResult, null);
            SetupException.RequireRepository(options.RepositoryRoot);

            // As the user named it, relative to the root the hook runs from.
            var solution = parseResult.GetValue(common.Solution) is { Length: > 0 } named
                ? Path.GetRelativePath(options.RepositoryRoot, Path.GetFullPath(named)).Replace('\\', '/')
                : null;

            var outcome = GitHook.Install(options.RepositoryRoot, kind, solution);
            if (!outcome.Done)
            {
                Console.Error.WriteLine($"  {outcome.Message}");
                return ExitCodes.Failed;
            }

            Console.Out.WriteLine($"  {outcome.Message}");
            Console.Out.WriteLine($"  Skip it once with `git {(kind == "pre-commit" ? "commit" : "push")} --no-verify`. With `dotnet tia server start`, its analysis takes about a second.");
            return ExitCodes.Ok;
        });

        var uninstall = new Command("uninstall", "Remove the hook `hook install` wrote. A hook it did not write is left alone.");
        uninstall.Options.Add(common.Path);
        uninstall.Options.Add(prePush);
        uninstall.Options.Add(preCommit);
        uninstall.SetAction(parseResult =>
        {
            if (Kind(parseResult, prePush, preCommit) is not { } kind)
            {
                return ExitCodes.Inconclusive;
            }

            var options = common.Read(parseResult, null);
            SetupException.RequireRepository(options.RepositoryRoot);

            var outcome = GitHook.Uninstall(options.RepositoryRoot, kind);
            (outcome.Done ? Console.Out : Console.Error).WriteLine($"  {outcome.Message}");
            return outcome.Done ? ExitCodes.Ok : ExitCodes.Failed;
        });

        hook.Subcommands.Add(install);
        hook.Subcommands.Add(uninstall);
        return hook;
    }

    private static string? Kind(ParseResult parseResult, Option<bool> prePush, Option<bool> preCommit)
    {
        if (parseResult.GetValue(prePush) && parseResult.GetValue(preCommit))
        {
            Console.Error.WriteLine("  --pre-push and --pre-commit are one hook each; pick one.");
            return null;
        }

        return parseResult.GetValue(preCommit) ? "pre-commit" : "pre-push";
    }
}
