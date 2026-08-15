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
    private readonly PhaseClock _clock = clock;

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

    public static List<TestMethod> SelectTests(
        IReadOnlyList<TestMethod> allTests,
        ImpactTraversal traversal,
        IReadOnlySet<string> widenedProjects)
    {
        var selected = new List<TestMethod>();

        foreach (var test in allTests)
        {
            if (widenedProjects.Contains(test.ProjectName) ||
                traversal.Impacted.Contains(test.SymbolKey) ||
                traversal.Impacted.Contains(test.ClassKey))
            {
                selected.Add(test);
            }
        }

        return selected;
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
        Stopwatch stopwatch)
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

        foreach (var unmapped in changes.UnmappedChanges)
        {
            widenings.Add(new WideningEvent("Unmapped", unmapped, "changed lines could not be mapped onto a declaration"));
        }

        var projects = new List<ProjectSelection>();
        var byProject = selected.GroupBy(t => t.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var allByProject = allTests.GroupBy(t => t.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var descriptor in descriptors.Where(d => d.IsTestProject))
        {
            var projectSelected = byProject.GetValueOrDefault(descriptor.Name, []);
            var projectAll = allByProject.GetValueOrDefault(descriptor.Name, []);

            if (projectSelected.Count == 0)
            {
                continue;
            }

            var dialect = FilterDialects.For(descriptor.Framework, descriptor.Runner);
            var plan = FilterPlanner.Plan(dialect, projectSelected, projectAll, options.MaxFilterLength, options.CoverageThreshold);

            if (plan.ExtraMatches.Count > 0)
            {
                widenings.Add(new WideningEvent("FilterDialect", descriptor.Name,
                    $"the {dialect.Name} filter also matches {plan.ExtraMatches.Count} test(s) outside the selection"));
            }

            if (!plan.Filtered && plan.UnfilteredReason is not null)
            {
                widenings.Add(new WideningEvent("Unfiltered", descriptor.Name, plan.UnfilteredReason));
            }

            projects.Add(new ProjectSelection
            {
                Name = descriptor.Name,
                ProjectPath = descriptor.FilePath,
                AssemblyPath = descriptor.OutputFilePath,
                Framework = descriptor.Framework.ToString(),
                Runner = descriptor.Runner.ToString(),
                PropertySource = descriptor.PropertiesEvaluated ? "evaluated" : "project-xml",
                TotalTests = projectAll.Count,
                SelectedTests = plan.Filtered ? projectSelected.Count : projectAll.Count,
                Filtered = plan.Filtered,
                UnfilteredReason = plan.UnfilteredReason,
                FilterArguments = plan.Arguments,
                Tests = [.. projectSelected.Select(t => t.FullyQualifiedName).Order(StringComparer.Ordinal)],
            });
        }

        return new AnalysisReport
        {
            Mode = "selective",
            DotnetTestMode = GlobalJson.ReadTestMode(options.RepositoryRoot).ToString(),
            MSBuild = WorkspaceLoader.RegisteredMSBuild,
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
            Timings = _clock.Snapshot(),
            TotalTests = allTests.Count,
            ImpactedTests = projects.Sum(p => p.Tests.Count),
            SelectedTests = projects.Sum(p => p.SelectedTests),
            Projects = projects,
            Diagnostics = [.. changes.Notes],
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
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
        IReadOnlyList<string>? notes = null)
    {
        var allByProject = allTests.GroupBy(t => t.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var projects = descriptors
            .Where(d => d.IsTestProject)
            .Select(d =>
            {
                var tests = allByProject.GetValueOrDefault(d.Name, []);
                return new ProjectSelection
                {
                    Name = d.Name,
                    ProjectPath = d.FilePath,
                    AssemblyPath = d.OutputFilePath,
                    Framework = d.Framework.ToString(),
                    Runner = d.Runner.ToString(),
                    PropertySource = d.PropertiesEvaluated ? "evaluated" : "project-xml",
                    TotalTests = tests.Count,
                    SelectedTests = tests.Count,
                    Filtered = false,
                    UnfilteredReason = "full run",
                };
            })
            .ToList();

        return new AnalysisReport
        {
            Mode = "full",
            DotnetTestMode = GlobalJson.ReadTestMode(options.RepositoryRoot).ToString(),
            MSBuild = WorkspaceLoader.RegisteredMSBuild,
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
            Timings = _clock.Snapshot(),
            TotalTests = allTests.Count,
            ImpactedTests = allTests.Count,
            SelectedTests = allTests.Count,
            Projects = projects,
            // How the diff was read is still worth saying when the answer is "everything runs":
            // a note that three of the changed files moved no token explains why a full run was
            // forced by the fourth. These were dropped on every full run.
            Diagnostics = notes ?? [],
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        };
    }
}
