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
                // Discovered *after* the checkout, and per commit, when one was not configured.
                // A path pinned once is a path pinned to today's layout: a solution renamed or
                // moved inside the walked range then resolves against a tree that does not contain
                // it, and every commit before the rename is skipped. That is how a replay of 20
                // commits once reported on 1.
                var solution = options.SolutionPath ?? WorkspaceLoader.FindSolutionOrProject(options.RepositoryRoot);

                string[] restoreArguments = solution is { Length: > 0 }
                    ? ["restore", solution, "-p:NuGetAudit=false"]
                    : ["restore", "-p:NuGetAudit=false"];

                var restore = ProcessRunner.Run("dotnet", restoreArguments, options.RepositoryRoot, cancellationToken: cancellationToken);

                if (!restore.Succeeded)
                {
                    // The reason, not just the fact. "restore failed, skipping" on every commit
                    // says the replay found nothing and gives no way to tell an unbuildable
                    // history from a broken harness.
                    var reason = FirstErrorLine(restore.StandardOutput) ?? FirstErrorLine(restore.StandardError) ?? "no error line captured";
                    _log($"{Short(commit)}: restore failed, skipping - {reason}");
                    continue;
                }

                var outcome = await new SolutionAnalyzer(options with { BaseRef = parent, DefaultBranch = null })
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

    private List<(string Commit, string Subject)> ReadCommits(int count, bool preferMerges)
    {
        var limit = count.ToString(CultureInfo.InvariantCulture);

        if (preferMerges)
        {
            var merges = ParseLog(Git("log", "--merges", "-n", limit, "--format=%H%x1f%s"));
            if (merges.Count > 0)
            {
                return merges;
            }
        }

        return ParseLog(Git("log", "--first-parent", "--no-merges", "-n", limit, "--format=%H%x1f%s"));
    }

    private static List<(string, string)> ParseLog(string output)
    {
        var commits = new List<(string, string)>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('', 2);
            if (parts.Length == 2)
            {
                commits.Add((parts[0].Trim(), parts[1].Trim()));
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
