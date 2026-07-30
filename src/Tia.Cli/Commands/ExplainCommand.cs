using System.CommandLine;
using Tia.Core.Model;
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
            var options = common.Read(parseResult, parseResult.GetValue(common.Verbose) ? Console.Error.WriteLine : null);
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

            foreach (var test in matches)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine($"  {test.FullyQualifiedName}");

                var widened = outcome.Report.Widenings.FirstOrDefault(w => w.Scope == test.ProjectName);
                var path = traversal.PathTo(test.SymbolKey);

                if (path.Count == 0)
                {
                    path = traversal.PathTo(test.ClassKey);
                }

                if (path.Count > 0)
                {
                    Console.Out.WriteLine("    selected - reached from a changed symbol:");
                    Console.Out.WriteLine();
                    RenderPath(graph, path);
                }
                else if (widened is not null)
                {
                    Console.Out.WriteLine($"    selected - its project was widened to full scope ({widened.Cause}: {widened.Detail})");
                }
                else
                {
                    Console.Out.WriteLine("    not selected - no path from any changed symbol reaches it.");
                }
            }

            Console.Out.WriteLine();
            return 0;
        });

        return command;
    }

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
