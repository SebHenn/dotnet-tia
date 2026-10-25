using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>One step of a path: the symbol, and how the step before it reached it.</summary>
public sealed record ExplainStep(string Symbol, string? Via);

/// <summary>What <c>explain</c> concluded about one test.</summary>
public sealed record ExplainedTest
{
    public required string Test { get; init; }

    public required string Project { get; init; }

    public required ExplainCommand.Verdict Verdict { get; init; }

    public string? UnfilteredReason { get; init; }

    /// <summary>From a changed symbol to the test, first step first; empty when nothing reaches it.</summary>
    public IReadOnlyList<ExplainStep> Path { get; init; } = [];

    /// <summary>The project's widenings, which an unreached test is told do not cover it.</summary>
    public IReadOnlyList<WideningEvent> Widenings { get; init; } = [];

    public bool WillRun => Verdict is ExplainCommand.Verdict.Reached or ExplainCommand.Verdict.ProjectRunsUnfiltered;
}

/// <summary>
/// Everything <c>explain</c> prints, computed from an analysis and nothing else.
/// </summary>
/// <remarks>
/// Separate from the printing so that <c>tia server</c> can compute it next to its workspace and
/// send back only this (#96). The graph and the traversal it reads stay on the server: display names
/// and edge descriptions are resolved here, so the client needs neither.
/// </remarks>
public sealed record ExplainResult
{
    public required string Query { get; init; }

    /// <summary>How many tests the analysis discovered.</summary>
    public required int Discovered { get; init; }

    /// <summary>Tests whose name ends with the query. None means nothing matched.</summary>
    public IReadOnlyList<string> Matches { get; init; } = [];

    /// <summary>When nothing matched: tests whose name contains the query.</summary>
    public IReadOnlyList<string> Containing { get; init; } = [];

    public bool FullRun { get; init; }

    public IReadOnlyList<string> FullRunReasons { get; init; } = [];

    public IReadOnlyList<ExplainedTest> Tests { get; init; } = [];

    /// <summary>Set when the analysis left nothing to explain from.</summary>
    public string? Error { get; init; }
}

public static class ExplainQuery
{
    public static ExplainResult Run(AnalysisOutcome outcome, string query)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(query);

        var matches = outcome.AllTests
            .Where(t => t.FullyQualifiedName.EndsWith(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var result = new ExplainResult
        {
            Query = query,
            Discovered = outcome.AllTests.Count,
            Matches = [.. matches.Select(t => t.FullyQualifiedName)],
        };

        if (matches.Count == 0)
        {
            // A suffix match is what answers "why this test", but the name people reach for first
            // is the class - and a class name is not a suffix of anything, so the exact query most
            // likely to be typed produced a bare "no test matches". Naming the near misses costs
            // nothing and turns a dead end into the next command to run.
            return result with
            {
                Containing =
                [
                    .. outcome.AllTests
                        .Where(t => t.FullyQualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .Select(t => t.FullyQualifiedName)
                        .Order(StringComparer.Ordinal),
                ],
            };
        }

        if (outcome.Report.IsFullRun)
        {
            return result with { FullRun = true, FullRunReasons = outcome.Report.FullRunReasons };
        }

        if (outcome.Traversal is not { } traversal || outcome.Graph is not { } graph)
        {
            return result with { Error = "No traversal is available for this analysis." };
        }

        var explained = new List<ExplainedTest>();
        foreach (var test in matches)
        {
            // The report is the authority on what will run, not the widening list. Any widening on
            // the project used to be read as "the project runs whole", but most are nothing of the
            // kind - a Reflection widening seeds the reflecting member and a FilterDialect widening
            // notes a few extra matches, and neither selects the project. On NodaTime that made
            // explain answer "selected" for a test that was genuinely missing from the selection,
            // which is the one answer this command must never get wrong.
            var selection = outcome.Report.Projects.FirstOrDefault(p => p.Name == test.ProjectName);
            var path = traversal.PathTo(test.SymbolKey);

            if (path.Count == 0)
            {
                path = traversal.PathTo(test.ClassKey);
            }

            var verdict = ExplainCommand.VerdictFor(path.Count > 0, selection);

            explained.Add(new ExplainedTest
            {
                Test = test.FullyQualifiedName,
                Project = test.ProjectName,
                Verdict = verdict,
                UnfilteredReason = verdict == ExplainCommand.Verdict.ProjectRunsUnfiltered ? selection!.UnfilteredReason : null,
                Path = [.. path.Select(step => new ExplainStep(
                    graph.TryGetNode(step.Key)?.DisplayName ?? step.Key,
                    step.IncomingEdge == EdgeKind.None ? null : Describe(step.IncomingEdge)))],
                Widenings = [.. outcome.Report.Widenings.Where(w => w.Scope == test.ProjectName)],
            });
        }

        return result with { Tests = explained };
    }

    /// <summary>
    /// Edge kinds are flags and an edge often carries several, so the most explanatory one wins
    /// rather than falling through to the generic label.
    /// </summary>
    internal static string Describe(EdgeKind kind)
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

        if (kind.HasFlag(EdgeKind.SharedTestName))
        {
            return "overload -> the test reported under its name";
        }

        if (kind.HasFlag(EdgeKind.Construction))
        {
            return "constructed by a container or framework -> what names its type";
        }

        if (kind.HasFlag(EdgeKind.ExternalDispatch))
        {
            return "dispatched from outside the solution, where the type is handed over";
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

        if (kind.HasFlag(EdgeKind.DispatchByRoute))
        {
            return "endpoint -> a member naming its route";
        }

        if (kind.HasFlag(EdgeKind.DispatchByRequest))
        {
            return "handler -> the request type that selects it";
        }

        return "referenced by";
    }
}
