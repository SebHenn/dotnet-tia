using System.Globalization;
using System.Text;
using Tia.Core.Infrastructure;

namespace Tia.Workspace.Harness;

public sealed record ReplayRow
{
    public required string Commit { get; init; }

    public required string Subject { get; init; }

    public required int Selected { get; init; }

    public required int Total { get; init; }

    public required bool FullRun { get; init; }

    public required int Widenings { get; init; }

    public required double Seconds { get; init; }

    public double Ratio => Total == 0 ? 1 : (double)Selected / Total;
}

/// <summary>
/// Replays real history to measure what selection is actually worth on a given repository.
/// </summary>
/// <remarks>
/// This harness answers a different question from the mutation harness and cannot replace it:
/// real commits are almost all green, so a replay proves nothing about misses. What it does show
/// is the selection ratio and the widening rate - and the widening rate is the number that
/// determines whether the tool is worth adopting on a reflection-heavy codebase, so it is
/// reported rather than buried.
/// </remarks>
public sealed class ReplayBenchmark(AnalysisOptions options, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <param name="preferMerges">
    /// Merge commits are the right unit on a repository whose pull requests land as merges. On one
    /// that periodically merges long-lived branches into each other they are the wrong unit -
    /// those merges carry months of work and say nothing about what selection would have done for
    /// a single change - so first-parent commits are replayed instead.
    /// </param>
    public async Task<IReadOnlyList<ReplayRow>> RunAsync(
        int commitCount,
        bool preferMerges = true,
        CancellationToken cancellationToken = default)
    {
        // Only tracked modifications matter: a checkout would clobber those, while untracked
        // files - build output, the .tia cache this very run writes - survive it untouched.
        var status = Git("status", "--porcelain", "--untracked-files=no");
        if (status.Trim().Length > 0)
        {
            throw new InvalidOperationException(
                "the working tree has modified tracked files; replay checks out historical commits and would discard them");
        }

        var startingPoint = Git("rev-parse", "--abbrev-ref", "HEAD").Trim();
        if (startingPoint == "HEAD")
        {
            startingPoint = Git("rev-parse", "HEAD").Trim();
        }

        var commits = ReadCommits(commitCount, preferMerges);
        var rows = new List<ReplayRow>();
        string? lastSolution = null;

        try
        {
            foreach (var (commit, subject) in commits)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parent = Git("rev-parse", "--verify", "--quiet", commit + "^1").Trim();
                if (parent.Length == 0)
                {
                    continue;
                }

                Git("checkout", "--quiet", "--detach", commit);

                // NuGetAudit off. Replay checks out revisions from months or years ago, and an
                // advisory published since then fires on every one of them - on NodaTime, a
                // transitive SharpCompress advisory plus TreatWarningsAsErrors failed the restore
                // for all 25 commits, none of which had anything to do with the code being
                // replayed. Whether a historical revision shipped a package that was later found
                // vulnerable is not a question this benchmark asks.
                // A solution, not the working directory. Restoring in the repository root only
                // works when the solution happens to sit there - NodaTime's is in src/, so every
                // commit failed with MSB1003 and the replay silently produced an empty table. Same
                // shape as the diff paths that were once resolved against --path instead of the
                // git root: a layout assumption that holds for the fixture and for nothing else.
                //
                // Found *after* the checkout, and per commit. A path pinned once is a path pinned
                // to today's layout: a solution renamed or moved inside the walked range resolves
                // against a tree that does not contain it, and every commit before the rename is
                // skipped. That is how a replay of 20 commits once reported on 1, which is why a
                // commit whose solution is missing now says so rather than vanishing.
                string? solution;
                string? missing;
                try
                {
                    solution = FindSolution(options.RepositoryRoot, options.SolutionPath, out missing);
                }
                catch (AmbiguousSolutionException ex)
                {
                    // A fact about this commit's layout, not about the replay: the commits either
                    // side of it may hold one solution and replay normally. Skipped with the reason,
                    // like a failed restore, rather than aborting the walk (issue #37).
                    _log($"{Short(commit)}: skipping - {ex.Message}");
                    continue;
                }

                if (solution is null)
                {
                    _log($"{Short(commit)}: skipping - {missing}");
                    continue;
                }

                if (!string.Equals(solution, lastSolution, StringComparison.Ordinal))
                {
                    _log($"{Short(commit)}: replaying {Path.GetRelativePath(options.RepositoryRoot, solution)}");
                    lastSolution = solution;
                }

                var restore = ProcessRunner.Run("dotnet", ["restore", solution, "-p:NuGetAudit=false"], options.RepositoryRoot, cancellationToken: cancellationToken);

                if (!restore.Succeeded)
                {
                    // The reason, not just the fact. "restore failed, skipping" on every commit
                    // says the replay found nothing and gives no way to tell an unbuildable
                    // history from a broken harness.
                    var reason = FirstErrorLine(restore.StandardOutput) ?? FirstErrorLine(restore.StandardError) ?? "no error line captured";
                    _log($"{Short(commit)}: restore failed, skipping - {reason}");
                    continue;
                }

                var outcome = await new SolutionAnalyzer(options with { BaseRef = parent, DefaultBranch = null, SolutionPath = solution })
                    .AnalyzeAsync(cancellationToken).ConfigureAwait(false);

                var report = outcome.Report;
                rows.Add(new ReplayRow
                {
                    Commit = Short(commit),
                    Subject = subject,
                    Selected = report.SelectedTests,
                    Total = report.TotalTests,
                    FullRun = report.IsFullRun,
                    Widenings = report.Widenings.Count,
                    Seconds = report.ElapsedSeconds,
                });

                _log($"{Short(commit)}  {report.SelectedTests}/{report.TotalTests}  {(report.IsFullRun ? "full" : "selective")}");
            }
        }
        finally
        {
            Git("checkout", "--quiet", "--force", startingPoint);
        }

        return rows;
    }

    /// <summary>Renders the table the README carries, including the widening rate.</summary>
    public static string ToMarkdown(IReadOnlyList<ReplayRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Commit | Selected | Full | Mode | Widenings |");
        builder.AppendLine("|---|---:|---:|---|---:|");

        foreach (var row in rows)
        {
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| `{row.Commit}` | {row.Selected} | {row.Total} | {(row.FullRun ? "full" : "selective")} | {row.Widenings} |"));
        }

        if (rows.Count == 0)
        {
            return builder.ToString();
        }

        var meanRatio = rows.Average(r => r.Ratio);
        var fullRunRate = (double)rows.Count(r => r.FullRun) / rows.Count;
        var wideningRate = (double)rows.Count(r => r.Widenings > 0) / rows.Count;

        builder.AppendLine();
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Mean selection **{meanRatio:P1}** · full-run rate **{fullRunRate:P0}** · commits with a widening **{wideningRate:P0}** · {rows.Count} commits"));

        return builder.ToString();
    }

    /// <summary>
    /// The solution to replay in the checked-out tree, or null with the reason there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A named solution is taken as given and only checked for, since it is the one the caller
    /// asked about. Otherwise discovery runs as it does for every command, in
    /// <paramref name="directory"/> itself, and then one level below it. That second step is
    /// replay's alone: every other command analyses the tree in front of the user, who can see
    /// where the solution is, while replay walks trees nobody is looking at. NodaTime keeps its
    /// solution in <c>src/</c>, and <c>replay -p .</c> skipped every commit with MSB1003 (#139).
    /// </para>
    /// <para>
    /// Only solution files count below, not a lone project: one level below a repository root is
    /// where projects live, one to a folder, so every folder would offer one.
    /// </para>
    /// </remarks>
    /// <exception cref="AmbiguousSolutionException">
    /// <paramref name="directory"/> itself holds several solutions of one kind.
    /// </exception>
    public static string? FindSolution(string directory, string? named, out string? reason)
    {
        reason = null;

        if (named is not null)
        {
            if (File.Exists(named))
            {
                return named;
            }

            reason = $"{named} does not exist at this commit";
            return null;
        }

        if (WorkspaceLoader.FindSolutionOrProject(directory) is { } here)
        {
            return here;
        }

        var below = new List<string>();
        foreach (var subdirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(subdirectory);
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules")
            {
                continue;
            }

            foreach (var pattern in (string[])["*.slnx", "*.sln", "*.slnf"])
            {
                var matches = Directory.GetFiles(subdirectory, pattern, SearchOption.TopDirectoryOnly);
                if (matches.Length > 0)
                {
                    below.AddRange(matches.Order(StringComparer.Ordinal));
                    break;
                }
            }
        }

        if (below.Count == 1)
        {
            return below[0];
        }

        reason = below.Count == 0
            ? $"no solution or project in {directory} or one level below it. Pass --path to the directory that " +
              "holds one, or --solution to name it"
            : $"several solutions one level below {directory} ({string.Join(", ", below.Select(b => Path.GetRelativePath(directory, b)))}). " +
              "Pass --solution to name one";
        return null;
    }

    /// <summary>
    /// Why the merges about to be replayed are a poor sample, or null when they are not.
    /// </summary>
    /// <remarks>
    /// Merges are the default unit, and on a history that merges rarely they are the wrong one: the
    /// newest merge can be years older than any recent change. On NodaTime <c>--commits 1</c>
    /// replayed one that pinned a preview SDK, failed to restore, and nothing suggested
    /// <c>--first-parent</c> (#139).
    /// </remarks>
    /// <param name="newestMerge">The newest merge's commit time, in Unix seconds.</param>
    /// <param name="oldestRecent">
    /// The commit time of the oldest of the same number of first-parent commits, in Unix seconds.
    /// </param>
    public static string? StaleMergeWarning(long newestMerge, long oldestRecent, int recentCount) =>
        newestMerge >= oldestRecent
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"the newest merge ({DateTimeOffset.FromUnixTimeSeconds(newestMerge):yyyy-MM-dd}) is older than the last " +
                $"{recentCount} first-parent commits; this history rarely merges, so pass --first-parent to replay what changed recently");

    private List<(string Commit, string Subject)> ReadCommits(int count, bool preferMerges)
    {
        var limit = count.ToString(CultureInfo.InvariantCulture);
        var linear = ParseLog(Git("log", "--first-parent", "--no-merges", "-n", limit, "--format=%H%x1f%ct%x1f%s"));

        if (preferMerges)
        {
            var merges = ParseLog(Git("log", "--merges", "-n", limit, "--format=%H%x1f%ct%x1f%s"));
            if (merges.Count > 0)
            {
                if (linear.Count > 0 && StaleMergeWarning(merges[0].Time, linear[^1].Time, linear.Count) is { } warning)
                {
                    _log(warning);
                }

                return [.. merges.Select(m => (m.Commit, m.Subject))];
            }

            _log("no merge commits; replaying first-parent commits instead");
        }

        return [.. linear.Select(c => (c.Commit, c.Subject))];
    }

    private static List<(string Commit, long Time, string Subject)> ParseLog(string output)
    {
        var commits = new List<(string, long, string)>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\u001f', 3);
            if (parts.Length == 3 && long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var time))
            {
                commits.Add((parts[0].Trim(), time, parts[2].Trim()));
            }
        }

        return commits;
    }

    private string Git(params string[] args)
    {
        var result = ProcessRunner.Run("git", args, options.RepositoryRoot);
        return result.StandardOutput;
    }

    /// <summary>The first line that looks like an error, for a log line rather than a dump.</summary>
    private static string? FirstErrorLine(string? output)
    {
        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains(": error ", StringComparison.Ordinal))
            {
                return trimmed.Length > 200 ? trimmed[..200] + "..." : trimmed;
            }
        }

        return null;
    }

    private static string Short(string commit) => commit.Length > 9 ? commit[..9] : commit;
}
