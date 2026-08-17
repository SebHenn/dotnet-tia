using System.CommandLine;
using System.Text.Json;
using Tia.Core.Reporting;

namespace Tia.Cli.Commands;

/// <summary>
/// What selection has actually cost or saved here, from runs that happened rather than from a
/// projection.
/// </summary>
/// <remarks>
/// The question this answers - <i>is this tool worth using on my repository?</i> - was previously
/// answerable only by arithmetic the reader had to do themselves, against a suite time the tool
/// never measured. Every other number in the report is about one diff; this one is about whether to
/// keep the tool.
/// </remarks>
public static class StatsCommand
{
    public static Command Create(CommonOptions common)
    {
        var command = new Command("stats", "Report what selection has actually cost or saved on this repository.");

        // No base ref, no full-run switch, no type flow: this reads a file and computes nothing
        // about a diff. An option that would be ignored is a parse error here, as everywhere else.
        common.AddTo(
            command,
            common.Base,
            common.Full,
            common.DefaultBranch,
            common.TypeFlow,
            common.NoCache,
            common.NoFallbackFull,
            common.MaxFilterLength,
            common.CoverageThreshold);

        command.SetAction(parseResult =>
        {
            var options = common.Read(parseResult, null);
            var cacheDirectory = Path.IsPathRooted(options.CacheDirectory)
                ? options.CacheDirectory
                : Path.Combine(options.RepositoryRoot, options.CacheDirectory);

            var records = RunLedger.Read(cacheDirectory);
            var verdict = RunLedger.Assess(records);

            if (parseResult.GetValue(common.Json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new { runs = records.Count, verdict, advice = verdict is null ? null : RunLedger.Advice(verdict) },
                    AnalysisReport.JsonOptions));

                return 0;
            }

            Console.Out.WriteLine();

            if (verdict is null)
            {
                // Deliberately not an error, and deliberately specific about what is missing. "No
                // data" would leave a reader wondering whether the feature works.
                Console.Out.WriteLine(records.Count == 0
                    ? "  No runs recorded yet. `dotnet tia run` records one per invocation; this needs at least three."
                    : $"  {records.Count} run(s) recorded, which is not enough to judge - this needs at least three, " +
                      "including one selective run whose suite actually executed.");
                Console.Out.WriteLine();
                return 0;
            }

            Console.Out.Write(RunLedger.Format(verdict));
            Console.Out.WriteLine();

            Console.Out.WriteLine(RunLedger.Advice(verdict) is { } advice
                ? $"  {advice}"
                : "  Selection is paying off here. Re-check with `dotnet tia stats` if your suite or its shape changes.");

            Console.Out.WriteLine();
            return 0;
        });

        return command;
    }
}
