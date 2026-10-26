using System.Diagnostics;
using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks;
using Tia.Frameworks.Dialects;

namespace Tia.Workspace;

/// <summary>
/// Turns a traversal into the set of tests to run, and that into the report a user reads.
/// </summary>
/// <remarks>
/// The two report shapes are kept together because they have to agree. A reader compares a full
/// run against a selective one on the same numbers - total tests, per-project counts, how the diff
/// was read - and a field one of them populates and the other silently drops reads as a change in
/// the answer rather than a change in the writer. That is not hypothetical: Diagnostics was set on
/// the selective path and left empty on the full one, so the notes explaining how the diff was
/// read vanished exactly when the tool had decided to run everything.
/// </remarks>
internal sealed class SelectionReporter(AnalysisOptions options, PhaseClock clock)
{
    /// <summary>
    /// The solution as a reader would type it from the repository root: relative, and with forward
    /// slashes, so the same analysis reports the same string on every platform. A solution given
    /// outside the root keeps its <c>..</c> segments rather than being shortened into something
    /// that names a different file.
    /// </summary>
    internal static string? DescribeSolution(string repositoryRoot, string? solutionPath) =>
        solutionPath is { Length: > 0 }
            ? Path.GetRelativePath(repositoryRoot, Path.GetFullPath(solutionPath)).Replace('\\', '/')
            : null;

    public static IReadOnlySet<string> ExpandToDependents(
        IReadOnlyList<ProjectDescriptor> descriptors,
        IReadOnlyList<ProjectWideChange> projectWide)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            foreach (var reference in descriptor.ProjectReferences)
            {
                if (!dependents.TryGetValue(reference, out var list))
                {
                    list = [];
                    dependents[reference] = list;
                }

                list.Add(descriptor.Name);
            }
        }

        var widened = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(projectWide
            .Where(c => c.WidensProject)
            .Select(c => c.ProjectName)
            .Distinct(StringComparer.Ordinal));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!widened.Add(current))
            {
                continue;
            }

            foreach (var dependent in dependents.GetValueOrDefault(current, []))
            {
                queue.Enqueue(dependent);
            }
        }

        return widened;
    }

    public AnalysisReport BuildSelectiveReport(
        DiffResult diff,
        string? headCommit,
        IReadOnlyList<ProjectDescriptor> descriptors,
        IReadOnlyList<TestMethod> allTests,
        IReadOnlyList<TestMethod> selected,
        SymbolChangeSet changes,
        int changedSymbolCount,
        IReadOnlySet<string> widenedProjects,
        GraphSummary graphSummary,
        Stopwatch stopwatch,
        IReadOnlyList<string>? loadFailures = null)
    {
        var widenings = new List<WideningEvent>();

        foreach (var change in changes.ProjectWide)
        {
            widenings.Add(new WideningEvent(change.Cause.ToString(), change.ProjectName, change.Detail));
        }

        foreach (var project in widenedProjects)
        {
            if (changes.ProjectWide.All(c => c.ProjectName != project))
            {
                widenings.Add(new WideningEvent("Dependent", project, "depends on a project that was widened to full scope"));
            }
        }

        var projects = new List<ProjectSelection>();
        var skipped = new List<SkippedTests>();
        var byProject = selected.GroupBy(t => t.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var allByProject = allTests.GroupBy(t => t.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var descriptor in descriptors.Where(d => d.IsTestProject))
        {
            var projectSelected = byProject.GetValueOrDefault(descriptor.Name, []);
            var projectAll = allByProject.GetValueOrDefault(descriptor.Name, []);

            // Last run's failures lead, the rest keep their rank behind them (#72). A stable sort,
            // so within each half the nearest test is still first.
            var lastFailed = options.LastFailed;
            if (lastFailed.Count > 0 && projectSelected.Any(t => lastFailed.Contains(t.FullyQualifiedName)))
            {
                projectSelected = [.. projectSelected.OrderBy(t => lastFailed.Contains(t.FullyQualifiedName) ? 0 : 1)];
            }

            if (projectSelected.Count == 0)
            {
                // Nothing selected, so every test of the project is left out.
                if (options.ReportSkipped && projectAll.Count > 0)
                {
                    skipped.Add(Skipped(descriptor.Name, projectAll, ran: []));
                }

                continue;
            }

            var dialect = FilterDialects.For(descriptor.Framework, descriptor.Runner);
            var plan = FilterPlanner.Plan(dialect, projectSelected, projectAll, options.MaxFilterLength, options.CoverageThreshold,
                filterFile: FilterFiles.Allowed(descriptor.Runner, descriptor.HasRunSettings) ? descriptor.Runner : null);

            if (plan.ExtraMatches.Count > 0)
            {
                widenings.Add(new WideningEvent("FilterDialect", descriptor.Name,
                    $"the {dialect.Name} filter also matches {plan.ExtraMatches.Count} test(s) outside the selection"));
            }

            if (options.ReportSkipped && plan.Filtered)
            {
                skipped.Add(Skipped(descriptor.Name, projectAll, ran: [.. projectSelected, .. plan.ExtraMatches]));
            }

            if (!plan.Filtered && plan.UnfilteredReason is not null)
            {
                widenings.Add(new WideningEvent("Unfiltered", descriptor.Name, plan.UnfilteredReason));
            }

            // Only a filtered project. An unfiltered one has no filter to narrow, so its first wave
            // could only be a second, overlapping invocation of tests the whole run repeats - which
            // costs the wave twice to save the same latency. That is a different trade with a
            // different cost model, and it is deliberately not made here.
            var failedWave = lastFailed.Count > 0
                ? WavePlanner.PlanLastFailed(dialect, projectSelected, projectAll, lastFailed, plan.Filtered, options.MaxFilterLength)
                : null;

            var wave = failedWave is { Split: true }
                ? failedWave
                : plan.Filtered
                    ? WavePlanner.Plan(dialect, projectSelected, projectAll, options.MaxFilterLength)
                    : null;

            projects.Add(SelectionFor(
                descriptor,
                total: projectAll.Count,
                selected: plan.Filtered ? projectSelected.Count : projectAll.Count,
                filtered: plan.Filtered) with
            {
                UnfilteredReason = plan.UnfilteredReason,
                FilterArguments = plan.Arguments,
                FilterInFile = plan.InFile,
                FirstWave = wave is { Split: true }
                    ? new RunWave
                    {
                        TestCount = wave.FirstWaveTests,
                        FilterArguments = wave.FirstWaveArguments,
                        RemainderFilterArguments = wave.RemainderArguments,
                        LastFailed = wave.LastFailed,
                    }
                    : null,
                UnsplitReason = wave is { Split: false } ? wave.Reason : null,
                // In the order SelectTests produced - nearest to the change first - not
                // alphabetical. Sorting by name here was harmless while nothing carried an order
                // and would now discard the rank, which is the whole point of computing it. The
                // order is still deterministic: ties inside a rank break on the name.
                Tests = [.. projectSelected.Select(t => t.FullyQualifiedName)],
            });
        }

        return new AnalysisReport
        {
            Mode = "selective",
            // Populated on this path too, and not because it can be: a load failure forces a full
            // run, so this is always empty here. The two report shapes have to agree field for
            // field - a field one populates and the other silently drops reads as a change in the
            // answer - and a future reason to reach here with one is a reason it must survive.
            LoadFailures = loadFailures ?? [],
            DotnetTestMode = GlobalJson.ReadTestMode(options.RepositoryRoot).ToString(),
            MSBuild = WorkspaceLoader.RegisteredMSBuild,
            Solution = DescribeSolution(options.RepositoryRoot, options.SolutionPath),
            BaseRef = options.BaseRef,
            BaseCommit = diff.BaseCommit,
            HeadCommit = headCommit,
            Widenings = widenings,
            Diff = new DiffSummary
            {
                FileCount = diff.Files.Count,
                CSharpFileCount = diff.Files.Count(f => f.IsCSharp),
                ChangedSymbolCount = changedSymbolCount,
                Files = [.. diff.Files.Select(f => f.ToString())],
            },
            Graph = graphSummary,
            Timings = clock.Snapshot(),
            TotalTests = allTests.Count,
            ImpactedTests = projects.Sum(p => p.Tests.Count),
            SelectedTests = projects.Sum(p => p.SelectedTests),
            Projects = projects,
            Diagnostics = [.. changes.Notes],
            SkippedTests = options.ReportSkipped ? [.. skipped.Where(s => s.Tests.Count > 0)] : null,
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        };
    }

    internal static ProjectSelection SelectionFor(ProjectDescriptor d, int total, int selected, bool filtered) => new()
    {
        Name = d.Name,
        ProjectPath = d.FilePath,
        AssemblyPath = d.OutputFilePath,
        Framework = d.Framework.ToString(),
        Runner = d.Runner.ToString(),
        TargetFramework = d.TargetFramework,
        PropertySource = d.PropertiesEvaluated ? "evaluated" : "project-xml",
        WritesTrx = d.WritesTrx,
        HasRunSettings = d.HasRunSettings,
        TotalTests = total,
        SelectedTests = selected,
        Filtered = filtered,
    };

    private static SkippedTests Skipped(string project, IReadOnlyList<TestMethod> all, IReadOnlyList<TestMethod> ran)
    {
        var running = new HashSet<string>(ran.Select(t => t.FullyQualifiedName), StringComparer.Ordinal);
        return new SkippedTests
        {
            Project = project,
            Tests = [.. all.Select(t => t.FullyQualifiedName).Where(n => !running.Contains(n)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
    }

    public AnalysisReport FullRunReport(
        IReadOnlyList<string> reasons,
        IReadOnlyList<ProjectDescriptor> descriptors,
        IReadOnlyList<TestMethod> allTests,
        Stopwatch stopwatch,
        GraphSummary? graphSummary = null,
        DiffResult? diff = null,
        string? headCommit = null,
        IReadOnlyList<string>? notes = null,
        IReadOnlyList<string>? loadFailures = null)
    {
        var allByProject = allTests.GroupBy(t => t.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var projects = descriptors
            .Where(d => d.IsTestProject)
            .Select(d =>
            {
                var tests = allByProject.GetValueOrDefault(d.Name, []);
                return SelectionFor(d, total: tests.Count, selected: tests.Count, filtered: false) with { UnfilteredReason = "full run" };
            })
            .ToList();

        return new AnalysisReport
        {
            Mode = "full",
            LoadFailures = loadFailures ?? [],
            DotnetTestMode = GlobalJson.ReadTestMode(options.RepositoryRoot).ToString(),
            MSBuild = WorkspaceLoader.RegisteredMSBuild,
            Solution = DescribeSolution(options.RepositoryRoot, options.SolutionPath),
            BaseRef = options.BaseRef,
            BaseCommit = diff?.BaseCommit,
            HeadCommit = headCommit,
            FullRunReasons = reasons,
            Diff = new DiffSummary
            {
                FileCount = diff?.Files.Count ?? 0,
                CSharpFileCount = diff?.Files.Count(f => f.IsCSharp) ?? 0,
                ChangedSymbolCount = 0,
                Files = [.. (diff?.Files ?? []).Select(f => f.ToString())],
            },
            Graph = graphSummary ?? new GraphSummary
            {
                Types = 0, Members = 0, Edges = 0, FromCache = false, ProjectsRebuilt = 0, ProjectsReused = 0,
            },
            Timings = clock.Snapshot(),
            TotalTests = allTests.Count,
            ImpactedTests = allTests.Count,
            SelectedTests = allTests.Count,
            Projects = projects,
            // How the diff was read is still worth saying when the answer is "everything runs":
            // a note that three of the changed files moved no token explains why a full run was
            // forced by the fourth. These were dropped on every full run.
            Diagnostics = notes ?? [],
            SkippedTests = options.ReportSkipped ? [] : null,
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        };
    }
}
