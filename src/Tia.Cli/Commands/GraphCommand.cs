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

        command.SetAction(async (parseResult, cancellationToken) =>
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
                graph,
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
                return 0;
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine($"  {graph.Types:N0} types / {graph.Members:N0} members / {graph.Edges:N0} edges");
            Console.Out.WriteLine($"  {graph.ProjectsRebuilt} project(s) built, {graph.ProjectsReused} reused from cache");
            Console.Out.WriteLine($"  {outcome.AllTests.Count:N0} tests discovered across {outcome.Report.Projects.Count} test project(s)");
            Console.Out.WriteLine($"  {outcome.Report.ElapsedSeconds:0.0}s");
            Console.Out.WriteLine();

            if (parseResult.GetValue(output) is { Length: > 0 } written)
            {
                Console.Out.WriteLine($"  written to {written}");
                Console.Out.WriteLine();
            }

            return 0;
        });

        return command;
    }
}
