using System.Diagnostics;
using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Safety;

namespace Tia.Workspace;

public sealed record AnalysisOutcome
{
    public required AnalysisReport Report { get; init; }

    /// <summary>Present in selective mode. <c>explain</c> replays the traversal from here.</summary>
    public ImpactGraph? Graph { get; init; }

    public ImpactTraversal? Traversal { get; init; }

    public IReadOnlyList<TestMethod> AllTests { get; init; } = [];

    public IReadOnlyList<ProjectDescriptor> Projects { get; init; } = [];
}

/// <summary>
/// The whole pipeline: diff, workspace, graph, selection, filters. Every early exit produces a
/// full-run report with a stated reason rather than an exception.
/// </summary>
/// <remarks>
/// Orchestration only. Each phase lives in its own type - <see cref="GraphBuilder"/>,
/// <see cref="ChangeResolver"/>, <see cref="ReflectionSeeder"/>, <see cref="SelectionReporter"/> -
/// so what is left here is the order they run in and the four points where the answer becomes "run
/// everything". That order is the part worth reading in one piece: every bail-out has to happen
/// before the work it would make pointless, and the graph is built even on the way to a full run
/// because `graph` exists to warm the cache.
/// </remarks>
public sealed class SolutionAnalyzer(AnalysisOptions options)
{
    private readonly Action<string> _log = options.Log ?? (_ => { });

    private readonly PhaseClock _clock = new();

    /// <summary>
    /// Captured as soon as the workspace has been loaded, so that a failure later in the run can
    /// still produce a full-run report that names what to execute.
    /// </summary>
    private IReadOnlyList<ProjectDescriptor> _loadedDescriptors = [];

    private IReadOnlyList<TestMethod> _loadedTests = [];

    /// <summary>
    /// Both report shapes. Constructed per use rather than held as a field: a field initializer
    /// cannot see <c>_clock</c>, and handing it a second clock would produce reports whose timings
    /// were all zero.
    /// </summary>
    private SelectionReporter Reports => new(options, _clock);

    public async Task<AnalysisOutcome> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            return await RunAsync(stopwatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (options.FallbackFullOnError)
        {
            // Whatever was learned before the failure. A full run that cannot name its test
            // projects is not a full run - `run` has nothing to invoke and would otherwise report
            // success having executed nothing - so the descriptors are captured as soon as the
            // workspace yields them and reused here.
            return new AnalysisOutcome
            {
                Report = Reports.FullRunReport(
                    [$"analysis failed, falling back to a full run: {ex.GetType().Name}: {ex.Message}"],
                    _loadedDescriptors,
                    _loadedTests,
                    stopwatch),
                AllTests = _loadedTests,
                Projects = _loadedDescriptors,
            };
        }
    }

    private async Task<AnalysisOutcome> RunAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var git = GitClient.Discover(options.RepositoryRoot)
                  ?? throw new InvalidOperationException($"'{options.RepositoryRoot}' is not inside a git repository.");

        var earlyReasons = new List<string>();

        if (options.ForceFull)
        {
            earlyReasons.Add("a full run was requested explicitly");
        }

        var branch = git.CurrentBranch();
        if (options.DefaultBranch is { Length: > 0 } defaultBranch &&
            string.Equals(branch, defaultBranch, StringComparison.Ordinal))
        {
            earlyReasons.Add($"HEAD is on the default branch '{defaultBranch}' - selection is a pull-request optimisation only");
        }

        var resolution = _clock.Time(nameof(PhaseTimings.DiffSeconds), () => new DiffResolver(git).Resolve(options.BaseRef));
        if (resolution.FullRunReason is not null)
        {
            earlyReasons.Add(resolution.FullRunReason);
        }

        earlyReasons.AddRange(FullRunTriggers.Scan(resolution.Diff?.Files ?? []));

        var solutionPath = options.SolutionPath
                           ?? WorkspaceLoader.FindSolutionOrProject(options.RepositoryRoot)
                           ?? throw new InvalidOperationException(
                               $"no solution or project found in '{options.RepositoryRoot}'. Pass --solution.");

        _log($"Loading {Path.GetFileName(solutionPath)}...");
        var loadStarted = Stopwatch.GetTimestamp();
        using var workspace = await WorkspaceLoader.LoadAsync(solutionPath, options.RepositoryRoot, _log, _clock, cancellationToken)
            .ConfigureAwait(false);
        _clock.Record(nameof(PhaseTimings.WorkspaceLoadSeconds), loadStarted);

        var descriptors = workspace.Projects.Select(p => p.Descriptor).ToList();
        _loadedDescriptors = descriptors;

        foreach (var failure in workspace.Failures)
        {
            // An SDK too old for the project it was pointed at is the one load failure that is
            // about the toolchain rather than the project, and its raw diagnostic reads as a
            // complaint about the target framework. Naming the registered MSBuild turns it into
            // something the reader can act on; everything else is reported as it arrived.
            earlyReasons.Add(SdkMismatch.Describe(failure, WorkspaceLoader.RegisteredVersion)
                             ?? $"a project failed to load, so its tests cannot be reasoned about: {failure}");
        }

        // The graph is needed even for a full run: `graph` warms it, and reporting the totals is
        // what makes the selection ratio meaningful.
        var graphStarted = Stopwatch.GetTimestamp();
        var (graph, allTests, graphSummary, compileErrors, reflections, routes, typeFacts, fragments) =
            new GraphBuilder(options, _log, _clock).Build(workspace, solutionPath, cancellationToken);
        _clock.Record(nameof(PhaseTimings.GraphSeconds), graphStarted);
        _loadedTests = allTests;

        // A project that does not bind is a project whose tests cannot be reasoned about. The
        // verdict travels with the cached fragment, so an unchanged project is never re-checked.
        earlyReasons.AddRange(compileErrors);

        if (earlyReasons.Count > 0 || resolution.Diff is null)
        {
            return new AnalysisOutcome
            {
                Report = Reports.FullRunReport(earlyReasons, descriptors, allTests, stopwatch, graphSummary, resolution.Diff, git.HeadCommit()),
                Graph = graph,
                AllTests = allTests,
                Projects = descriptors,
            };
        }

        var diff = resolution.Diff;
        var changeStarted = Stopwatch.GetTimestamp();
        var changes = new ChangeResolver(_log, _clock)
            .Resolve(workspace, git, diff, graph, fragments, out var compilationErrors, cancellationToken);
        _clock.Record(nameof(PhaseTimings.ChangeResolutionSeconds), changeStarted);

        if (compilationErrors.Count > 0)
        {
            return new AnalysisOutcome
            {
                Report = Reports.FullRunReport(compilationErrors, descriptors, allTests, stopwatch, graphSummary, diff, git.HeadCommit(), changes.Notes),
                Graph = graph,
                AllTests = allTests,
                Projects = descriptors,
            };
        }

        // Counted before reflection seeds anything. Those are not symbols the diff changed, and
        // reporting "3 symbols changed" for a one-line edit is exactly the kind of small untruth
        // that makes someone stop trusting the rest of the numbers.
        var changedSymbolCount = changes.Keys.Count;

        // Before the traversal, because it adds edges the traversal has to be able to follow. The
        // join is cross-project, so it can only happen once every fragment has been merged in.
        var routeStarted = Stopwatch.GetTimestamp();
        RouteSeeder.WidenChangedTemplates(routes, diff, changes);
        var routeEdges = RouteSeeder.Seed(graph, routes, cancellationToken);
        _clock.Record(nameof(PhaseTimings.RouteSeedSeconds), routeStarted);
        if (routeEdges > 0)
        {
            changes.Notes.Add($"{routeEdges} route-dispatch edge(s) joined an endpoint to a member naming its route");
        }

        // Also after the merge, and for the same reason: what a member can obtain through the
        // members it calls is a fact about the whole solution, and a fragment only sees its own
        // project. Reflecting members are handed over as already unknown - the scan that found
        // them has run, and "this member defeats static analysis" is the same verdict both uses.
        var typeFlow = options.TypeFlow
            ? _clock.Time(nameof(PhaseTimings.TypeFlowResolveSeconds), () => TypeFlowIndex.Resolve(
                graph,
                typeFacts,
                reflections.Select(r => r.Record.OwningMemberKey).OfType<string>(),
                cancellationToken))
            : null;

        if (typeFlow is not null)
        {
            changes.Notes.Add(
                $"type flow bounded {typeFlow.BoundedTypes} implementing type(s)" +
                (typeFlow.SaturatedNodes > 0
                    ? $"; {typeFlow.SaturatedNodes} member(s) reached too many types to track and permit every hop"
                    : string.Empty));
        }

        var selector = new ImpactSelector(typeFlow);
        var traversal = _clock.Time(nameof(PhaseTimings.SelectionSeconds),
            () => new ReflectionSeeder().Seed(selector, graph, reflections, changes, cancellationToken));

        if (typeFlow is not null)
        {
            changes.Notes.Add($"type flow narrowed {selector.NarrowedHops} interface hop(s)");
        }

        var widenedProjects = SelectionReporter.ExpandToDependents(descriptors, changes.ProjectWide);
        var selected = TestSelection.InRunOrder(allTests, traversal, widenedProjects);

        var report = Reports.BuildSelectiveReport(
            diff, git.HeadCommit(), descriptors, allTests, selected, changes, changedSymbolCount,
            widenedProjects, graphSummary, stopwatch);

        return new AnalysisOutcome
        {
            Report = report,
            Graph = graph,
            Traversal = traversal,
            AllTests = allTests,
            Projects = descriptors,
        };
    }
}
