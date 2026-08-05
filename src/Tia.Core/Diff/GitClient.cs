using Tia.Core.Infrastructure;

namespace Tia.Core.Diff;

public sealed class GitClient : IGitClient
{
    private readonly string _workingDirectory;
    private bool? _isShallow;

    private GitClient(string workingDirectory, string repositoryRoot)
    {
        _workingDirectory = workingDirectory;
        RepositoryRoot = repositoryRoot;
    }

    public string RepositoryRoot { get; }

    public bool IsShallow => _isShallow ??=
        string.Equals(Run("rev-parse", "--is-shallow-repository").StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Discovers the repository containing <paramref name="path"/>, or returns null.</summary>
    /// <remarks>
    /// <para>
    /// The root is reported in the caller's own path namespace, not git's. <c>--show-toplevel</c>
    /// resolves symlinks; MSBuild keeps whatever path it was handed. Under a symlinked checkout the
    /// two disagree - macOS puts temporary directories under <c>/var</c>, which is a symlink to
    /// <c>/private/var</c>, and a junction on Windows or a symlinked CI workspace does the same
    /// thing - so every diff path was combined with a root no document could match.
    /// </para>
    /// <para>
    /// Nothing then reported a problem, because nothing had gone wrong as far as any single step
    /// could tell: the diff resolved, the workspace loaded, and zero files matched zero documents.
    /// The run selected no tests, <c>run</c> printed "nothing was impacted by this diff" and exited
    /// 0. A silent miss reported as success is the one outcome this tool exists to make impossible,
    /// and it needed no exotic setup - analysing a repository through a symlink was enough.
    /// </para>
    /// <para>
    /// Asking how deep the caller is and ascending that far from the path it actually used avoids
    /// canonicalising anything: git already knows the depth, and the answer stays in the namespace
    /// every later comparison is made in.
    /// </para>
    /// </remarks>
    public static GitClient? Discover(string path)
    {
        var probe = new GitClient(path, path);
        var result = probe.Run("rev-parse", "--show-toplevel");
        if (!result.Succeeded)
        {
            return null;
        }

        var toplevel = result.StandardOutput.Trim();
        if (string.IsNullOrEmpty(toplevel))
        {
            return null;
        }

        // "" when the caller is at the root, "tests/Fixtures/" when it is two directories below it.
        var prefix = probe.Run("rev-parse", "--show-prefix");
        var root = prefix.Succeeded ? AscendToRoot(path, prefix.StandardOutput.Trim()) : null;

        return new GitClient(root ?? toplevel, root ?? toplevel);
    }

    /// <summary>
    /// Climbs out of <paramref name="path"/> by as many directories as <paramref name="prefix"/> is
    /// deep, giving the repository root as the caller would have spelled it.
    /// </summary>
    private static string? AscendToRoot(string path, string prefix)
    {
        var current = Path.GetFullPath(path);

        foreach (var _ in prefix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Path.GetDirectoryName(current) is not { Length: > 0 } parent)
            {
                return null;
            }

            current = parent;
        }

        return Directory.Exists(current) ? current : null;
    }

    public string? ResolveCommit(string revision)
    {
        var result = Run("rev-parse", "--verify", "--quiet", revision + "^{commit}");
        var text = result.StandardOutput.Trim();
        return result.Succeeded && text.Length > 0 ? text : null;
    }

    public string? MergeBase(string a, string b)
    {
        var result = Run("merge-base", a, b);
        var text = result.StandardOutput.Trim();
        return result.Succeeded && text.Length > 0 ? text : null;
    }

    public bool IsAncestor(string ancestor, string descendant) =>
        Run("merge-base", "--is-ancestor", ancestor, descendant).ExitCode == 0;

    public string NameStatus(string baseCommit) =>
        RunOrThrow("diff", "--name-status", "-M", "-z", baseCommit);

    public string Hunks(string baseCommit, IReadOnlyList<string> paths)
    {
        var args = new List<string> { "diff", "-U0", "-M", "--no-color", baseCommit, "--" };
        args.AddRange(paths);
        return RunOrThrow([.. args]);
    }

    public IReadOnlyList<string> UntrackedFiles() =>
    [
        .. RunOrThrow("ls-files", "--others", "--exclude-standard", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('\n', '\r').Replace('\\', '/'))
            .Where(p => p.Length > 0),
    ];

    public string? ShowFile(string revision, string path)
    {
        var result = Run("show", $"{revision}:{path}");
        return result.Succeeded ? result.StandardOutput : null;
    }

    public string? CurrentBranch()
    {
        var result = Run("rev-parse", "--abbrev-ref", "HEAD");
        var text = result.StandardOutput.Trim();
        return result.Succeeded && text.Length > 0 ? text : null;
    }

    public string? HeadCommit() => ResolveCommit("HEAD");

    /// <summary>
    /// How long a git command may take before it is killed.
    /// </summary>
    /// <remarks>
    /// Every command here answers in milliseconds on any repository. The reasons one would not
    /// are all reasons to stop rather than to keep waiting: a credential helper prompting on a
    /// terminal nobody is watching, a stale <c>index.lock</c> left by a crashed editor, a network
    /// filesystem that has gone away. Unbounded, the CLI hangs with no output and no explanation,
    /// which in CI means a job that burns its whole time budget.
    /// </remarks>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    /// <remarks>
    /// <c>GIT_TERMINAL_PROMPT=0</c> turns the commonest cause of a hang into an immediate failure
    /// with a message, which is a better answer than a two-minute wait. <c>GIT_OPTIONAL_LOCKS=0</c>
    /// stops read-only commands taking the index lock at all, so they neither block on another
    /// git process nor leave a lock behind if killed.
    /// </remarks>
    private static readonly Dictionary<string, string> NonInteractive = new(StringComparer.Ordinal)
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    private ProcessResult Run(params string[] args) =>
        ProcessRunner.Run("git", args, _workingDirectory, timeout: CommandTimeout, environment: NonInteractive);

    private string RunOrThrow(params string[] args)
    {
        var result = Run(args);
        if (!result.Succeeded)
        {
            throw new GitException($"git {string.Join(' ', args)} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }
}

public sealed class GitException(string message) : Exception(message);
