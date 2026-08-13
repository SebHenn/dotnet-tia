using System.Globalization;
using System.CommandLine;
using System.Text;
using Tia.Workspace;
using Tia.Workspace.Harness;

namespace Tia.Validation;

/// <summary>
/// Drives the two validation harnesses against a checked-out repository. Kept out of the shipped
/// tool because these runs are measured in minutes to hours: they belong in a nightly job, not in
/// a pull-request loop.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        WorkspaceLoader.RegisterMSBuild();

        var repository = new Option<string>("--repo", "-r")
        {
            Description = "Repository to validate against.",
            Required = true,
        };

        var solution = new Option<string?>("--solution", "-s")
        {
            Description = "Solution or project to analyse. Discovered from the repository root when omitted.",
        };

        var output = new Option<string?>("--output", "-o")
        {
            Description = "Write the markdown report here instead of stdout.",
        };

        var samples = new Option<int>("--samples")
        {
            Description = "Mutation samples to run.",
            DefaultValueFactory = _ => 100,
        };

        var seed = new Option<int>("--seed")
        {
            Description = "Random seed, so a failing run can be replayed.",
            DefaultValueFactory = _ => 20260730,
        };

        var commits = new Option<int>("--commits")
        {
            Description = "Commits to replay.",
            DefaultValueFactory = _ => 50,
        };

        // The engine option the harnesses have to be able to reach, because the claim it makes is
        // about selection and selection is what these two measure. Without it the flag could only
        // ever be gated on the fixture solutions, which are far too small to show what it costs.
        var typeFlow = new Option<bool>("--type-flow")
        {
            Description = "Bound each interface hop by the concrete types a member can obtain, not merely reach.",
        };

        var firstParent = new Option<bool>("--first-parent")
        {
            Description = "Replay first-parent commits instead of merges. Use this when the "
                          + "repository merges long-lived branches into each other, so its merge "
                          + "commits carry months of work rather than one change.",
        };

        var mutate = new Command("mutate", "Mutation harness. Zero misses is the merge gate.")
        {
            repository, solution, output, samples, seed, typeFlow,
        };

        mutate.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = ReadOptions(parseResult, repository, solution, typeFlow);
            var harness = new MutationHarness(options, Console.Error.WriteLine);
            var result = await harness
                .RunAsync(parseResult.GetValue(samples), new Random(parseResult.GetValue(seed)), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await WriteAsync(parseResult.GetValue(output), RenderMutationReport(options, result), cancellationToken).ConfigureAwait(false);
            return result.Passed ? 0 : 1;
        });

        var replay = new Command("replay", "Commit-replay benchmark. Reports selection ratio and widening rate.")
        {
            repository, solution, output, commits, firstParent, typeFlow,
        };

        replay.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = ReadOptions(parseResult, repository, solution, typeFlow);
            var benchmark = new ReplayBenchmark(options, Console.Error.WriteLine);
            var rows = await benchmark
                .RunAsync(parseResult.GetValue(commits), preferMerges: !parseResult.GetValue(firstParent), cancellationToken)
                .ConfigureAwait(false);

            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"### Commit replay - {Path.GetFileName(options.RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar))}")
                .AppendLine()
                .Append(ReplayBenchmark.ToMarkdown(rows))
                .ToString();

            await WriteAsync(parseResult.GetValue(output), report, cancellationToken).ConfigureAwait(false);
            return 0;
        });

        var root = new RootCommand("Validation harnesses for dotnet tia.") { mutate, replay };
        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    private static AnalysisOptions ReadOptions(
        ParseResult parseResult,
        Option<string> repository,
        Option<string?> solution,
        Option<bool> typeFlow) => new()
    {
        RepositoryRoot = Path.GetFullPath(parseResult.GetValue(repository)!),
        BaseRef = "HEAD",
        SolutionPath = parseResult.GetValue(solution) is { Length: > 0 } path ? Path.GetFullPath(path) : null,
        UseCache = true,
        FallbackFullOnError = true,
        TypeFlow = parseResult.GetValue(typeFlow),
        Log = Console.Error.WriteLine,
    };

    private static string RenderMutationReport(AnalysisOptions options, MutationHarnessResult result)
    {
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"### Mutation harness - {Path.GetFileName(options.RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar))}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{result.Usable} usable sample(s), {result.Skipped} skipped, **{result.Misses} miss(es)**")
            .AppendLine();

        AppendSkipReasons(report, result);

        if (result.Misses == 0)
        {
            report.AppendLine(result.Usable == 0
                ? "No sample could be checked, so nothing was proved."
                : "No failing test was left out of a selection.");
            return report.ToString();
        }

        report.AppendLine("| Mutation | Site | Tests not selected |");
        report.AppendLine("|---|---|---|");

        foreach (var sample in result.Samples.Where(s => s.Misses.Count > 0))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"| {sample.Member} {sample.Description} | `{sample.File}:{sample.Line}` | {string.Join("<br>", sample.Misses.Take(5))} |");
        }

        return report.ToString();
    }

    /// <summary>
    /// Why the skipped samples skipped, whenever they outnumber the usable ones.
    /// </summary>
    /// <remarks>
    /// Without this the report said "0 usable sample(s), 20 skipped, 0 miss(es)" followed by "no
    /// failing test was left out of a selection" - a sentence that is true of a run which checked
    /// nothing, printed directly beneath the number saying so. The first MediatR run read exactly
    /// that way, and the cause (every analysis bailing out to a full run over an MSBuild warning)
    /// took a separate investigation to find because the report would not say it. `verify` has
    /// grouped its skip reasons since Phase 5; this is the same courtesy in markdown.
    /// </remarks>
    private static void AppendSkipReasons(StringBuilder report, MutationHarnessResult result)
    {
        if (result.Skipped <= result.Usable)
        {
            return;
        }

        var reasons = result.Samples
            .Where(s => s.SkipReason is { Length: > 0 })
            .GroupBy(s => s.SkipReason!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .ToList();

        if (reasons.Count == 0)
        {
            return;
        }

        report.AppendLine("Most samples were skipped:").AppendLine();

        foreach (var reason in reasons)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"- **{reason.Count()}x** {reason.Key}");
        }

        report.AppendLine();
    }

    private static async Task WriteAsync(string? path, string content, CancellationToken cancellationToken)
    {
        if (path is { Length: > 0 })
        {
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
            Console.Error.WriteLine($"written to {path}");
        }
        else
        {
            Console.Out.Write(content);
        }
    }
}
