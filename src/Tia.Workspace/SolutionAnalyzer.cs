using System.Diagnostics;
using Tia.Core.Analysis;
using Tia.Core.Caching;
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

    /// <summary>The type-flow bound the selection used, when <c>--type-flow</c> asked for one.</summary>
    public TypeFlowIndex? TypeFlow { get; init; }

    /// <summary>Whether a running <c>tia server</c> did this analysis, rather than this process (#97).</summary>
    public bool Served { get; init; }
}

/// <summary>
/// Where an analysis gets its workspace from. Null means open one, analyse, and dispose it, which
/// is what every one-shot command does; <c>tia watch</c> passes a delegate that refreshes the
/// workspace it has kept open since the first run.
/// </summary>
public delegate Task<LoadedWorkspace> WorkspaceSource(PhaseClock clock, CancellationToken cancellationToken);

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
public sealed class SolutionAnalyzer(AnalysisOptions options, WorkspaceSource? workspaceSource = null)
{
    private readonly Action<string> _log = options.Log ?? (_ => { });

    private readonly PhaseClock _clock = new();

    /// <summary>
    /// Captured as soon as the workspace has been loaded, so that a failure later in the run can
    /// still produce a full-run report that names what to execute.
    /// </summary>
    private IReadOnlyList<ProjectDescriptor> _loadedDescriptors = [];

    private IReadOnlyList<TestMethod> _loadedTests = [];

    /// <summary>Captured with the descriptors, for the same reason: a full run has to name them too.</summary>
    private IReadOnlyList<ForeignProject> _foreignProjects = [];

    /// <summary>
    /// The solution this run settled on, discovered or given. Held for the same reason as the
    /// descriptors: a report produced after a later failure still has to say what it analysed.
    /// </summary>
    private string? _solutionPath;

    /// <summary>
    /// Both report shapes. Constructed per use rather than held as a field: a field initializer
    /// cannot see <c>_clock</c>, and handing it a second clock would produce reports whose timings
    /// were all zero.
    /// </summary>
    private SelectionReporter Reports => new(options with { SolutionPath = _solutionPath ?? options.SolutionPath }, _clock);

    public async Task<AnalysisOutcome> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outcome = await RunAsync(stopwatch, cancellationToken).ConfigureAwait(false);

            // Every full-run report, whichever of the several early returns produced it: a full
            // run that leaves out the F# and VB test projects is not one (#44).
            return outcome.Report.IsFullRun
                ? outcome with { Report = outcome.Report with { UnanalysedTestProjects = UnanalysedTests.ForFullRun(_foreignProjects) } }
                : outcome;
        }

        // Not a failure to fall back from. A full run is a full run of the solution that was loaded,
        // and with no solution, or several and no choice, nothing was: the fallback report would
        // name no project, and `analyze` would print it and exit 0. The question goes to the
        // caller - each command turns it into exit 2 (#37, #50).
        catch (Exception ex) when (options.FallbackFullOnError && ex is not (SetupException or OperationCanceledException))
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
                    stopwatch) with
                {
                    UnanalysedTestProjects = UnanalysedTests.ForFullRun(_foreignProjects),
                },
                AllTests = _loadedTests,
                Projects = _loadedDescriptors,
            };
        }
    }

    private async Task<AnalysisOutcome> RunAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var git = SetupException.RequireRepository(options.RepositoryRoot);

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

        var resolution = _clock.Time(nameof(PhaseTimings.DiffSeconds), () => new DiffResolver(git, CacheDirectory.RelativeTo(git.RepositoryRoot, options.RepositoryRoot, options.CacheDirectory))
            .Resolve(options.BaseRef));
        if (resolution.FullRunReason is not null)
        {
            earlyReasons.Add(resolution.Diff is null && options.BaseRefHint is { } hint
                ? $"{resolution.FullRunReason} - {hint}"
                : resolution.FullRunReason);
        }

        earlyReasons.AddRange(FullRunTriggers.Scan(resolution.Diff?.Files ?? []));

        var solutionPath = SetupException.RequireSolution(options.RepositoryRoot, options.SolutionPath);
        _solutionPath = solutionPath;

        if (workspaceSource is null)
        {
            _log($"Loading {Path.GetFileName(solutionPath)}...");
        }

        // Disposing is right in both cases and only does something in one: a workspace handed over
        // by a resident process is owned by that process, and its `LoadedWorkspace` says so.
        var loadStarted = Stopwatch.GetTimestamp();
        using var workspace = workspaceSource is null
            ? await WorkspaceLoader.LoadAsync(solutionPath, options.RepositoryRoot, _log, _clock, cancellationToken).ConfigureAwait(false)
            : await workspaceSource(_clock, cancellationToken).ConfigureAwait(false);
        _clock.Record(nameof(PhaseTimings.WorkspaceLoadSeconds), loadStarted);

        var descriptors = workspace.Projects.Select(p => p.Descriptor).ToList();
        _loadedDescriptors = descriptors;
        _foreignProjects = workspace.ForeignProjects;

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
        var (graph, allTests, graphSummary, compileErrors, loadFailures, reflections, routes, typeFacts, fragments) =
            new GraphBuilder(options, _log, _clock, git.IsTracked).Build(workspace, solutionPath, cancellationToken);
        _clock.Record(nameof(PhaseTimings.GraphSeconds), graphStarted);
        _loadedTests = allTests;

        // A project that does not bind is a project whose tests cannot be reasoned about. The
        // verdict travels with the cached fragment, so an unchanged project is never re-checked.
        earlyReasons.AddRange(compileErrors);

        // Last, and after the graph rather than before it, because it is a question about what the
        // load produced rather than about what it complained of. A test project that resolved no
        // test symbols is the one state in which a full run is not a conservative answer but an
        // empty one - so it is carried separately from the reasons, and the commands refuse on it.
        earlyReasons.AddRange(loadFailures);

        if (earlyReasons.Count > 0 || resolution.Diff is null)
        {
            return new AnalysisOutcome
            {
                Report = Reports.FullRunReport(
                    earlyReasons, descriptors, allTests, stopwatch, graphSummary, resolution.Diff, git.HeadCommit(),
                    loadFailures: loadFailures),
                Graph = graph,
                AllTests = allTests,
                Projects = descriptors,
            };
        }

        var diff = resolution.Diff;
        var changeStarted = Stopwatch.GetTimestamp();
        var changes = new ChangeResolver(_clock)
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
        var changedKeys = changes.Keys.ToList();

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
            () => ReflectionSeeder.Seed(selector, graph, reflections, changes, cancellationToken));

        if (typeFlow is not null)
        {
            changes.Notes.Add($"type flow narrowed {selector.NarrowedHops} interface hop(s)");
        }

        var widenedProjects = SelectionReporter.ExpandToDependents(descriptors, changes.ProjectWide);
        var selected = TestSelection.InRunOrder(allTests, traversal, widenedProjects);

        var (untestedKeys, uncheckedChanges) = _clock.Time(nameof(PhaseTimings.UntestedSeconds),
            () => UntestedChanges.Find(graph, changedKeys, traversal, allTests, widenedProjects, new ImpactSelector(typeFlow),
                options.CheckEveryChange ? null : UntestedChanges.MaxTraversals, cancellationToken));

        var report = Reports.BuildSelectiveReport(
            diff, git.HeadCommit(), descriptors, allTests, selected, changes, changedSymbolCount,
            widenedProjects, graphSummary, stopwatch) with
        {
            UntestedChanges = [.. untestedKeys.Select(key => DescribeUntested(graph, key)).OrderBy(u => u.Symbol, StringComparer.Ordinal)],
            UncheckedChanges = uncheckedChanges,
            UnanalysedTestProjects = UnanalysedTests.ForSelectiveRun(
                workspace.ForeignProjects,
                descriptors,
                UnanalysedTests.AffectedProjects(
                    graph, traversal, widenedProjects, changes.ProjectWide.Where(c => c.WidensProject).Select(c => c.ProjectName))),
        };

        return new AnalysisOutcome
        {
            Report = report,
            Graph = graph,
            Traversal = traversal,
            AllTests = allTests,
            Projects = descriptors,
            TypeFlow = typeFlow,
        };
    }

    private UntestedChange DescribeUntested(ImpactGraph graph, string key)
    {
        var node = graph.TryGetNode(key)!;
        return new UntestedChange
        {
            Symbol = node.DisplayName,
            Project = node.ProjectName,
            File = node.FilePath is { Length: > 0 } file
                ? Path.GetRelativePath(options.RepositoryRoot, file).Replace('\\', '/')
                : null,
            Line = node.Line > 0 ? node.Line : null,
        };
    }
}
