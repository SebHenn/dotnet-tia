using System.CommandLine;
using System.Text.Json;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>
/// Shows the graph path from a changed symbol to a test.
/// </summary>
/// <remarks>
/// This is not a nice-to-have. The first question any adopter asks is "why did it pick this test",
/// or worse, "why didn't it", and a tool that cannot answer either question does not get trusted
/// with the decision to skip tests.
/// </remarks>
public static class ExplainCommand
{

    /// <summary>What `explain` concluded about one test.</summary>
    public enum Verdict
    {
        /// <summary>A path runs from a changed symbol to it.</summary>
        Reached,

        /// <summary>Nothing reaches it, but its project runs whole, so it executes regardless.</summary>
        ProjectRunsUnfiltered,

        /// <summary>Its project is not in the selection at all.</summary>
        ProjectAbsent,

        /// <summary>Nothing reaches it and nothing else will run it.</summary>
        NotReached,
    }

    /// <summary>
    /// The verdict, from the report rather than from the widening list.
    /// </summary>
    /// <remarks>
    /// Extracted so it can be tested, because this is the one answer the command must never get
    /// wrong and it got it wrong. Any widening on the project used to be read as "the project runs
    /// whole", but most are nothing of the kind: a Reflection widening seeds the reflecting member
    /// and a FilterDialect widening notes a few extra matches, and neither selects the project. On
    /// NodaTime that made `explain` report "selected" for a test genuinely missing from the
    /// selection - the tool whose job is answering "why" confidently answering wrongly.
    /// </remarks>
    public static Verdict VerdictFor(bool reachedByPath, ProjectSelection? selection) => (reachedByPath, selection) switch
    {
        (true, _) => Verdict.Reached,
        (false, null) => Verdict.ProjectAbsent,
        (false, { Filtered: false }) => Verdict.ProjectRunsUnfiltered,
        _ => Verdict.NotReached,
    };

    public static Command Create(CommonOptions common)
    {
        var testArgument = new Argument<string>("test")
        {
            Description = "Fully qualified test name, or a suffix of one.",
        };

        var command = new Command("explain", "Show why a test was, or was not, selected.");
        common.AddTo(command);
        command.Options.Add(common.NoServer);
        command.Arguments.Add(testArgument);

        command.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(common.Json);
            var options = common.Read(
                parseResult, parseResult.GetValue(common.Verbose) && !json ? Console.Error.WriteLine : null);
            var query = parseResult.GetValue(testArgument)!;

            return Render(await ExplainAsync(common, parseResult, options, query, cancellationToken).ConfigureAwait(false), json);
        }));

        return command;
    }

    /// <summary>
    /// From a running <c>tia server</c> when there is one here, which answers in about a second
    /// instead of loading the solution (#96), and in this process otherwise. The same answer
    /// either way: the server runs this same query against an analysis of the same tree.
    /// </summary>
    private static async Task<ExplainResult> ExplainAsync(
        CommonOptions common, ParseResult parseResult, AnalysisOptions options, string query, CancellationToken cancellationToken)
    {
        if (!parseResult.GetValue(common.NoServer) && File.Exists(Server.ServerProtocol.StatePath(options.ResolvedCacheDirectory)))
        {
            var withSolution = options with
            {
                SolutionPath = SetupException.RequireSolution(options.RepositoryRoot, options.SolutionPath),
            };

            if (await Server.ServerClient.TryExplainAsync(withSolution, query, options.Log, cancellationToken).ConfigureAwait(false) is { } served)
            {
                return served;
            }
        }

        var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        return ExplainQuery.Run(outcome, query);
    }

    private static int Render(ExplainResult result, bool json)
    {
        if (result.Matches.Count == 0)
        {
            if (json)
            {
                Console.Out.WriteLine(Serialize(new
                {
                    schemaVersion = AnalysisReport.SchemaVersion,
                    query = result.Query,
                    matched = 0,
                    discovered = result.Discovered,
                    containing = result.Containing,
                }));

                return 1;
            }

            Console.Error.WriteLine($"No test name ends with '{result.Query}'. {result.Discovered} tests were discovered.");

            if (result.Containing.Count > 0)
            {
                Console.Error.WriteLine($"  {result.Containing.Count} test(s) contain it:");
                foreach (var name in result.Containing.Take(10))
                {
                    Console.Error.WriteLine($"    {name}");
                }

                if (result.Containing.Count > 10)
                {
                    Console.Error.WriteLine($"    ... and {result.Containing.Count - 10} more");
                }
            }

            return 1;
        }

        if (result.FullRun)
        {
            if (json)
            {
                Console.Out.WriteLine(Serialize(new
                {
                    schemaVersion = AnalysisReport.SchemaVersion,
                    query = result.Query,
                    fullRun = true,
                    reasons = result.FullRunReasons,
                    tests = result.Matches,
                }));

                return 0;
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine("  Everything is running: selection was not applied.");
            foreach (var reason in result.FullRunReasons)
            {
                Console.Out.WriteLine($"    ! {reason}");
            }

            Console.Out.WriteLine();
            return 0;
        }

        if (result.Error is { } error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        if (json)
        {
            Console.Out.WriteLine(Serialize(new
            {
                schemaVersion = AnalysisReport.SchemaVersion,
                query = result.Query,
                fullRun = false,
                tests = result.Tests.Select(t => new
                {
                    test = t.Test,
                    project = t.Project,
                    verdict = t.Verdict.ToString(),
                    willRun = t.WillRun,
                    unfilteredReason = t.UnfilteredReason,
                    path = t.Path.Select(step => new { symbol = step.Symbol, via = step.Via }),
                }),
            }));

            return 0;
        }

        foreach (var test in result.Tests)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"  {test.Test}");

            switch (test.Verdict)
            {
                case Verdict.Reached:
                    Console.Out.WriteLine("    selected - reached from a changed symbol:");
                    Console.Out.WriteLine();
                    RenderPath(test.Path);
                    break;

                case Verdict.ProjectRunsUnfiltered:
                    Console.Out.WriteLine("    not selected by the graph, but it will run anyway:");
                    Console.Out.WriteLine($"    {test.Project} runs unfiltered ({test.UnfilteredReason ?? "no filter was emitted"}).");
                    break;

                case Verdict.ProjectAbsent:
                    Console.Out.WriteLine($"    not selected - {test.Project} is not in the selection at all.");
                    break;

                default:
                    Console.Out.WriteLine("    not selected - no path from any changed symbol reaches it.");

                    foreach (var widening in test.Widenings)
                    {
                        Console.Out.WriteLine($"      (its project has a {widening.Cause} widening, which does not cover this test: {widening.Detail})");
                    }

                    break;
            }
        }

        Console.Out.WriteLine();
        return 0;
    }

    private static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, AnalysisReport.JsonOptions);

    private static void RenderPath(IReadOnlyList<ExplainStep> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            if (i == 0)
            {
                Console.Out.WriteLine($"      {path[i].Symbol}   (changed)");
                continue;
            }

            Console.Out.WriteLine($"        |  {path[i].Via ?? "referenced by"}");
            Console.Out.WriteLine($"      {path[i].Symbol}");
        }
    }
}
