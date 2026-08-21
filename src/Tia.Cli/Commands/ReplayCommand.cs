using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tia.Core.Reporting;
using Tia.Workspace.Harness;

namespace Tia.Cli.Commands;

/// <summary>
/// Replays a repository's own history and reports what selection would have done on each commit.
/// </summary>
/// <remarks>
/// <para>
/// The one question a prospective adopter has is whether this would have paid off on <i>their</i>
/// commits, and until now answering it meant cloning this repository: the harness existed, was
/// compiled into the shipped package, and was reachable from nothing but an unshipped validation
/// tool.
/// </para>
/// <para>
/// That gap is not academic. The premise this tool's own performance work started from - that a
/// particular application could never benefit - stood unchallenged for months, and was wrong in
/// both of its terms. It stood because checking it was hard. A person who can run one command
/// against their own history does not have to take a README's word for it, and does not have to
/// take a maintainer's arithmetic on trust either.
/// </para>
/// </remarks>
public static class ReplayCommand
{
    public static Command Create(CommonOptions common)
    {
        var commits = new Option<int>("--commits")
        {
            Description = "How many commits to replay.",
            DefaultValueFactory = _ => 20,
        };

        var firstParent = new Option<bool>("--first-parent")
        {
            Description =
                "Replay first-parent commits instead of merges. Use on a repository that merges " +
                "long-lived branches into each other, where a merge carries months of work and " +
                "says nothing about what selection would do for one change.",
        };

        var output = new Option<string?>("--output", "-o")
        {
            Description = "Write the markdown report to this file instead of stdout.",
        };

        var command = new Command(
            "replay",
            "Replay this repository's history and report what selection would have done on each commit.");

        // No --base: each commit is measured against its own parent, which is the whole point.
        // No --full or --default-branch: both force a full run, so every row would read 100 %.
        //
        // And deliberately no --solution. Replay checks out historical revisions, so a path given
        // once is a path pinned to today's layout - a solution moved or renamed inside the walked
        // range then resolves against a tree that does not contain it, and the commits before the
        // move are silently skipped. Discovery runs per checkout instead.
        common.AddTo(command, common.Base, common.Full, common.DefaultBranch, common.Solution);
        command.Options.Add(commits);
        command.Options.Add(firstParent);
        command.Options.Add(output);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(common.Json);
            var verbose = parseResult.GetValue(common.Verbose);
            var options = common.Read(parseResult, verbose && !json ? Console.Error.WriteLine : null) with
            {
                // Each commit is compared against its own parent; the benchmark sets that per row.
                BaseRef = "HEAD",
                SolutionPath = null,
            };

            IReadOnlyList<ReplayRow> rows;

            try
            {
                rows = await new ReplayBenchmark(options, json ? null : m => Console.Error.WriteLine("  " + m))
                    .RunAsync(
                        parseResult.GetValue(commits),
                        preferMerges: !parseResult.GetValue(firstParent),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // The refusal to run on a dirty tree arrives this way. It is the safety property
                // that matters most here - this command checks out other commits - so it is
                // reported as a refusal rather than as a crash.
                Console.Error.WriteLine();
                Console.Error.WriteLine($"  {ex.Message}");
                Console.Error.WriteLine();
                return 1;
            }

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { Commits = rows, Summary = Summarise(rows) }, AnalysisReport.JsonOptions));
                return rows.Count == 0 ? 1 : 0;
            }

            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture,
                    $"### Commit replay - {Path.GetFileName(options.RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar))}")
                .AppendLine()
                .Append(ReplayBenchmark.ToMarkdown(rows))
                .ToString();

            if (parseResult.GetValue(output) is { Length: > 0 } path)
            {
                await File.WriteAllTextAsync(path, report, cancellationToken).ConfigureAwait(false);
                Console.Out.WriteLine();
                Console.Out.WriteLine($"  Wrote {path}");
            }
            else
            {
                Console.Out.WriteLine();
                Console.Out.Write(report);
            }

            if (rows.Count == 0)
            {
                // Zero rows is the shape a broken replay takes - an unbuildable history, a
                // solution that could not be found, a range with no parents. Reporting success
                // for it would publish "no data" as "no benefit".
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    "  No commit could be replayed. Nothing here says anything about selection on this " +
                    "repository; the reasons are on stderr above, one per commit.");
                Console.Error.WriteLine();
                return 1;
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine("  A replay measures selection ratio and widening rate. It says nothing about misses:");
            Console.Out.WriteLine("  real commits are almost all green, so nothing was there to be missed. Use `verify`");
            Console.Out.WriteLine("  or `shadow` for that.");
            Console.Out.WriteLine();

            return 0;
        });

        return command;
    }

    /// <summary>The three figures the markdown footer carries, for a caller reading <c>--json</c>.</summary>
    internal static object Summarise(IReadOnlyList<ReplayRow> rows) => new
    {
        Commits = rows.Count,
        MeanSelectedFraction = rows.Count == 0 ? 0 : Math.Round(rows.Average(r => r.Ratio), 4),
        FullRunFraction = rows.Count == 0 ? 0 : Math.Round((double)rows.Count(r => r.FullRun) / rows.Count, 4),
        WidenedFraction = rows.Count == 0 ? 0 : Math.Round((double)rows.Count(r => r.Widenings > 0) / rows.Count, 4),
        MeanAnalysisSeconds = rows.Count == 0 ? 0 : Math.Round(rows.Average(r => r.Seconds), 3),
    };
}
