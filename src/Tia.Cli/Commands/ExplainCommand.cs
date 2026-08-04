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
        command.Arguments.Add(testArgument);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(common.Json);
            var options = common.Read(
                parseResult, parseResult.GetValue(common.Verbose) && !json ? Console.Error.WriteLine : null);
            var query = parseResult.GetValue(testArgument)!;

            var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);

            var matches = outcome.AllTests
                .Where(t => t.FullyQualifiedName.EndsWith(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                // A suffix match is what answers "why this test", but the name people reach for
                // first is the class - and a class name is not a suffix of anything, so the exact
                // query most likely to be typed produced a bare "no test matches". Naming the
                // near misses costs nothing and turns a dead end into the next command to run.
                var near = outcome.AllTests
                    .Where(t => t.FullyQualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.FullyQualifiedName)
                    .Order(StringComparer.Ordinal)
                    .ToList();

                if (json)
                {
                    Console.Out.WriteLine(Serialize(new
                    {
                        query,
                        matched = 0,
                        discovered = outcome.AllTests.Count,
                        containing = near,
                    }));

                    return 1;
                }

                Console.Error.WriteLine($"No test name ends with '{query}'. {outcome.AllTests.Count} tests were discovered.");

                if (near.Count > 0)
                {
                    Console.Error.WriteLine($"  {near.Count} test(s) contain it:");
                    foreach (var name in near.Take(10))
                    {
                        Console.Error.WriteLine($"    {name}");
                    }

                    if (near.Count > 10)
                    {
                        Console.Error.WriteLine($"    ... and {near.Count - 10} more");
                    }
                }

                return 1;
            }

            if (outcome.Report.IsFullRun)
            {
                if (json)
                {
                    Console.Out.WriteLine(Serialize(new
                    {
                        query,
                        fullRun = true,
                        reasons = outcome.Report.FullRunReasons,
                        tests = matches.Select(t => t.FullyQualifiedName),
                    }));

                    return 0;
                }

                Console.Out.WriteLine();
                Console.Out.WriteLine("  Everything is running: selection was not applied.");
                foreach (var reason in outcome.Report.FullRunReasons)
                {
                    Console.Out.WriteLine($"    ! {reason}");
                }

                Console.Out.WriteLine();
                return 0;
            }

            var traversal = outcome.Traversal;
            var graph = outcome.Graph;
            if (traversal is null || graph is null)
            {
                Console.Error.WriteLine("No traversal is available for this analysis.");
                return 1;
            }

            var explained = new List<object>();

            foreach (var test in matches)
            {
                if (!json)
                {
                    Console.Out.WriteLine();
                    Console.Out.WriteLine($"  {test.FullyQualifiedName}");
                }

                // The report is the authority on what will run, not the widening list. Any
                // widening on the project used to be read as "the project runs whole", but most
                // are nothing of the kind - a Reflection widening seeds the reflecting member and
                // a FilterDialect widening notes a few extra matches, and neither selects the
                // project. On NodaTime that made explain answer "selected" for a test that was
                // genuinely missing from the selection, which is the one answer this command must
                // never get wrong.
                var selection = outcome.Report.Projects.FirstOrDefault(p => p.Name == test.ProjectName);
                var path = traversal.PathTo(test.SymbolKey);

                if (path.Count == 0)
                {
                    path = traversal.PathTo(test.ClassKey);
                }

                var verdict = VerdictFor(path.Count > 0, selection);

                if (json)
                {
                    explained.Add(new
                    {
                        test = test.FullyQualifiedName,
                        project = test.ProjectName,
                        verdict = verdict.ToString(),
                        willRun = verdict is Verdict.Reached or Verdict.ProjectRunsUnfiltered,
                        unfilteredReason = verdict == Verdict.ProjectRunsUnfiltered ? selection!.UnfilteredReason : null,
                        path = path.Select(step => new
                        {
                            symbol = graph.TryGetNode(step.Key)?.DisplayName ?? step.Key,
                            via = step.IncomingEdge == EdgeKind.None ? null : Describe(step.IncomingEdge),
                        }),
                    });

                    continue;
                }

                switch (verdict)
                {
                    case Verdict.Reached:
                        Console.Out.WriteLine("    selected - reached from a changed symbol:");
                        Console.Out.WriteLine();
                        RenderPath(graph, path);
                        break;

                    case Verdict.ProjectRunsUnfiltered:
                        Console.Out.WriteLine("    not selected by the graph, but it will run anyway:");
                        Console.Out.WriteLine($"    {test.ProjectName} runs unfiltered ({selection!.UnfilteredReason ?? "no filter was emitted"}).");
                        break;

                    case Verdict.ProjectAbsent:
                        Console.Out.WriteLine($"    not selected - {test.ProjectName} is not in the selection at all.");
                        break;

                    default:
                        Console.Out.WriteLine("    not selected - no path from any changed symbol reaches it.");

                        foreach (var widening in outcome.Report.Widenings.Where(w => w.Scope == test.ProjectName))
                        {
                            Console.Out.WriteLine($"      (its project has a {widening.Cause} widening, which does not cover this test: {widening.Detail})");
                        }

                        break;
                }
            }

            if (json)
            {
                Console.Out.WriteLine(Serialize(new { query, fullRun = false, tests = explained }));
                return 0;
            }

            Console.Out.WriteLine();
            return 0;
        });

        return command;
    }

    private static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, AnalysisReport.JsonOptions);

    private static void RenderPath(ImpactGraph graph, IReadOnlyList<(string Key, EdgeKind IncomingEdge)> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            var (key, edge) = path[i];
            var name = graph.TryGetNode(key)?.DisplayName ?? key;

            if (i == 0)
            {
                Console.Out.WriteLine($"      {name}   (changed)");
                continue;
            }

            Console.Out.WriteLine($"        |  {Describe(edge)}");
            Console.Out.WriteLine($"      {name}");
        }
    }

    /// <summary>
    /// Edge kinds are flags and an edge often carries several, so the most explanatory one wins
    /// rather than falling through to the generic label.
    /// </summary>
    private static string Describe(EdgeKind kind)
    {
        if (kind.HasFlag(EdgeKind.ImplementationToInterface))
        {
            return "implementation -> interface member";
        }

        if (kind.HasFlag(EdgeKind.InterfaceToImplementation))
        {
            return "interface member -> implementation";
        }

        if (kind.HasFlag(EdgeKind.OverrideToVirtual))
        {
            return "override -> virtual member";
        }

        if (kind.HasFlag(EdgeKind.VirtualToOverride))
        {
            return "virtual member -> override";
        }

        if (kind.HasFlag(EdgeKind.Fixture))
        {
            return "fixture -> test";
        }

        if (kind.HasFlag(EdgeKind.Derived))
        {
            return "base type -> derived type";
        }

        if (kind.HasFlag(EdgeKind.Containment))
        {
            return "type -> declared member";
        }

        if (kind.HasFlag(EdgeKind.Attribute))
        {
            return "attribute -> annotated symbol";
        }

        return "referenced by";
    }
}
