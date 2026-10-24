using System.CommandLine;
using System.Text.Json;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>
/// Builds and caches the graph without analysing a diff. In CI this is the warming step: run it
/// against the base branch, cache <c>.tia/</c>, and every pull-request analysis starts warm.
/// </summary>
public static class GraphCommand
{
    public static Command Create(CommonOptions common)
    {
        var output = new Option<string?>("--output", "-o")
        {
            Description = "Also write the graph summary and discovered tests to this JSON file.",
        };

        var command = new Command("graph", "Build or refresh the cached symbol graph.");

        // No diff is resolved here, so anything that describes one would be accepted and ignored.
        common.AddTo(command, common.Base, common.Full, common.DefaultBranch);
        command.Options.Add(output);

        command.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(common.Verbose);
            var options = common.Read(parseResult, verbose ? Console.Error.WriteLine : null) with
            {
                // The diff is irrelevant here; forcing a full run skips it and goes straight to
                // building the graph.
                ForceFull = true,
            };

            var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
            var graph = outcome.Report.Graph;

            var payload = new
            {
                schemaVersion = AnalysisReport.SchemaVersion,
                graph,
                // Carried into the file and the stream, not left to the exit code alone. A consumer
                // that reads this JSON is exactly the one that would otherwise treat an empty
                // `tests` array as a repository with no tests in it.
                loadFailures = outcome.Report.LoadFailures,
                tests = outcome.AllTests.Select(t => new { t.FullyQualifiedName, t.ProjectName, framework = t.Framework.ToString(), t.IsParameterized }),
            };

            if (parseResult.GetValue(output) is { Length: > 0 } file)
            {
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(payload, AnalysisReport.JsonOptions), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (parseResult.GetValue(common.Json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, AnalysisReport.JsonOptions));
                return Exit(outcome.Report);
            }

            // Invariant, like every other number this tool prints: ReportRenderer formats counts
            // and timings invariantly, and `graph` printing 1.035 where `analyze` prints 1,035 for
            // the same graph reads as two different numbers.
            Console.Out.WriteLine();
            Console.Out.WriteLine(FormattableString.Invariant(
                $"  {graph.Types:N0} types / {graph.Members:N0} members / {graph.Edges:N0} edges"));
            Console.Out.WriteLine($"  {graph.ProjectsRebuilt} project(s) built, {graph.ProjectsReused} reused from cache");
            Console.Out.WriteLine(FormattableString.Invariant(
                $"  {outcome.AllTests.Count:N0} tests discovered across {outcome.Report.Projects.Count} test project(s)"));
            Console.Out.WriteLine(FormattableString.Invariant($"  {outcome.Report.ElapsedSeconds:0.0}s"));
            Console.Out.WriteLine();

            if (parseResult.GetValue(output) is { Length: > 0 } written)
            {
                Console.Out.WriteLine($"  written to {written}");
                Console.Out.WriteLine();
            }

            // Printed after the counts rather than instead of them, because the counts are the
            // evidence: "0 tests discovered across 1 test project(s)" is the shape of the failure,
            // and a reader who has seen the number once is owed the sentence that reads it.
            foreach (var failure in outcome.Report.LoadFailures)
            {
                Console.Error.WriteLine($"  x {failure}");
            }

            if (outcome.Report.WorkspaceUnusable)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("  The graph above is a graph of a failed load. Nothing cached from it is usable.");
                Console.Error.WriteLine();
            }

            return Exit(outcome.Report);
        }));

        return command;
    }

    /// <summary>
    /// Non-zero when the load resolved no tests. This command warms a cache for every later
    /// analysis, so a silent success here is a silent success in every one of them - which is how
    /// a CI job wired the obvious way printed encouraging output on every pull request while
    /// selecting from an empty set.
    /// </summary>
    private static int Exit(AnalysisReport report) =>
        report.WorkspaceUnusable ? ExitCodes.Inconclusive : ExitCodes.Ok;
}
