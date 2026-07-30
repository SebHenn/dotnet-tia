using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Analysis;
using Tia.Core.Caching;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Safety;
using Tia.Frameworks;
using Tia.Frameworks.Dialects;

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
public sealed class SolutionAnalyzer(AnalysisOptions options)
{
    private readonly Action<string> _log = options.Log ?? (_ => { });

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
            return new AnalysisOutcome
            {
                Report = FullRunReport([$"analysis failed, falling back to a full run: {ex.GetType().Name}: {ex.Message}"], [], [], stopwatch),
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

        var resolution = new DiffResolver(git).Resolve(options.BaseRef);
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
        using var workspace = await WorkspaceLoader.LoadAsync(solutionPath, options.RepositoryRoot, _log, cancellationToken)
            .ConfigureAwait(false);

        var descriptors = workspace.Projects.Select(p => p.Descriptor).ToList();

        foreach (var failure in workspace.Failures)
        {
            earlyReasons.Add($"a project failed to load, so its tests cannot be reasoned about: {failure}");
        }

        earlyReasons.AddRange(FindBrokenProjects(workspace, cancellationToken));

        // The graph is needed even for a full run: `graph` warms it, and reporting the totals is
        // what makes the selection ratio meaningful.
        var (graph, allTests, graphSummary) = BuildGraph(workspace, solutionPath, cancellationToken);

        if (earlyReasons.Count > 0 || resolution.Diff is null)
        {
            return new AnalysisOutcome
            {
                Report = FullRunReport(earlyReasons, descriptors, allTests, stopwatch, graphSummary, resolution.Diff, git.HeadCommit()),
                Graph = graph,
                AllTests = allTests,
                Projects = descriptors,
            };
        }

        var diff = resolution.Diff;
        var changes = ResolveChanges(workspace, git, diff, out var compilationErrors, cancellationToken);

        if (compilationErrors.Count > 0)
        {
            return new AnalysisOutcome
            {
                Report = FullRunReport(compilationErrors, descriptors, allTests, stopwatch, graphSummary, diff, git.HeadCommit()),
                Graph = graph,
                AllTests = allTests,
                Projects = descriptors,
            };
        }

        var traversal = new ImpactSelector().Traverse(graph, changes.Keys, cancellationToken);

        ApplyReflectionWidening(workspace, graph, diff, traversal, changes, cancellationToken);

        var widenedProjects = ExpandToDependents(descriptors, changes.ProjectWide);
        var selected = SelectTests(allTests, traversal, widenedProjects);

        var report = BuildSelectiveReport(
            diff, git.HeadCommit(), descriptors, allTests, selected, changes, widenedProjects, graphSummary, stopwatch);

        return new AnalysisOutcome
        {
            Report = report,
            Graph = graph,
            Traversal = traversal,
            AllTests = allTests,
            Projects = descriptors,
        };
    }

    /// <summary>
    /// Projects whose declarations do not bind.
    /// </summary>
    /// <remarks>
    /// This is the check that catches an unrestored solution, and it matters more than it looks:
    /// a project with no references still parses, so test discovery finds nothing in it and the
    /// result looks like a clean, tiny selection rather than the blind spot it is.
    ///
    /// Declaration diagnostics rather than full diagnostics on purpose - binding signatures, base
    /// types and attributes is a fraction of the cost of binding every method body, and a missing
    /// reference always shows up at declaration level.
    /// </remarks>
    private static List<string> FindBrokenProjects(LoadedWorkspace workspace, CancellationToken cancellationToken)
    {
        var reasons = new List<string>();

        Parallel.ForEach(workspace.Projects, new ParallelOptions { CancellationToken = cancellationToken }, context =>
        {
            var error = context.Compilation
                .GetDeclarationDiagnostics(cancellationToken)
                .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);

            if (error is null)
            {
                return;
            }

            lock (reasons)
            {
                reasons.Add($"{context.Name} does not compile ({error.Id}: {error.GetMessage()})");
            }
        });

        reasons.Sort(StringComparer.Ordinal);
        return reasons;
    }

    // ---------------------------------------------------------------- graph

    private (ImpactGraph Graph, IReadOnlyList<TestMethod> Tests, GraphSummary Summary) BuildGraph(
        LoadedWorkspace workspace,
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var sdkVersion = RuntimeInformation.FrameworkDescription;
        var cachePath = Path.Combine(options.RepositoryRoot, options.CacheDirectory, GraphCache.FileName(solutionPath, sdkVersion));
        var cache = options.UseCache ? GraphCache.TryLoad(cachePath, sdkVersion) : null;
        var fresh = GraphCache.Empty(sdkVersion);

        var trackedAssemblies = new HashSet<string>(
            workspace.Projects.Select(p => p.Compilation.AssemblyName ?? p.Descriptor.AssemblyName),
            StringComparer.Ordinal);

        var fingerprints = ProjectFingerprint.ComputeAll(workspace.Projects);
        var builder = new ReferenceGraphBuilder(trackedAssemblies);
        var discoverer = new TestDiscoverer();

        var reused = 0;
        var rebuilt = 0;
        var fragments = new ProjectGraphFragment?[workspace.Projects.Count];

        Parallel.For(0, workspace.Projects.Count, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        }, index =>
        {
            var context = workspace.Projects[index];
            var fingerprint = fingerprints[context.Name];

            if (cache is not null &&
                cache.Projects.TryGetValue(context.Name, out var cached) &&
                string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                fragments[index] = cached;
                Interlocked.Increment(ref reused);
                return;
            }

            var projectGraph = builder.Build(context.Compilation, context.Name, cancellationToken);
            var tests = context.Descriptor.IsTestProject
                ? discoverer.Discover(context.Compilation, context.Name, context.Descriptor.Framework, projectGraph, cancellationToken)
                : [];

            fragments[index] = new ProjectGraphFragment
            {
                ProjectName = context.Name,
                Fingerprint = fingerprint,
                Graph = projectGraph,
                Tests = tests,
            };

            Interlocked.Increment(ref rebuilt);
        });

        var graph = new ImpactGraph();
        var allTests = new List<TestMethod>();

        foreach (var fragment in fragments)
        {
            if (fragment is null)
            {
                continue;
            }

            graph.Merge(fragment.Graph);
            allTests.AddRange(fragment.Tests);
            fresh.Projects[fragment.ProjectName] = fragment;
        }

        if (options.UseCache)
        {
            try
            {
                fresh.Save(cachePath);
            }
            catch (Exception ex)
            {
                _log($"could not write the graph cache: {ex.Message}");
            }
        }

        var summary = new GraphSummary
        {
            Types = graph.TypeCount,
            Members = graph.NodeCount - graph.TypeCount,
            Edges = graph.EdgeCount,
            FromCache = reused > 0,
            ProjectsRebuilt = rebuilt,
            ProjectsReused = reused,
        };

        return (graph, allTests, summary);
    }

    // ---------------------------------------------------------------- changes

    private SymbolChangeSet ResolveChanges(
        LoadedWorkspace workspace,
        IGitClient git,
        DiffResult diff,
        out List<string> compilationErrors,
        CancellationToken cancellationToken)
    {
        compilationErrors = [];
        var changes = new SymbolChangeSet();
        var resolver = new ChangedSymbolResolver();
        var typeIndexes = new Dictionary<string, SourceTypeIndex>(StringComparer.Ordinal);

        foreach (var file in diff.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var absolute = Path.GetFullPath(Path.Combine(options.RepositoryRoot, file.Path));

            if (!file.IsCSharp)
            {
                if (!ContentFileRules.IsWideningContent(file.Path))
                {
                    continue;
                }

                var owner = FindOwningProject(workspace, absolute);
                if (owner is not null)
                {
                    changes.AddProjectWide(owner.Name, ProjectWideCause.ContentFile,
                        $"{file.Path} is not source; nothing in the symbol graph connects it to the tests that read it");
                }

                continue;
            }

            // New side.
            var documentId = workspace.Solution.GetDocumentIdsWithFilePath(absolute).FirstOrDefault();
            if (documentId is not null && file.ExistsOnNewSide)
            {
                var document = workspace.Solution.GetDocument(documentId)!;
                var context = workspace.Projects.FirstOrDefault(p => p.Project.Id == document.Project.Id);

                if (context is not null)
                {
                    var tree = context.Compilation.SyntaxTrees.FirstOrDefault(t => PathsEqual(t.FilePath, absolute));
                    if (tree is not null)
                    {
                        var model = context.Compilation.GetSemanticModel(tree);

                        foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken))
                        {
                            if (diagnostic.Severity == DiagnosticSeverity.Error)
                            {
                                compilationErrors.Add($"{file.Path} does not compile ({diagnostic.Id}: {diagnostic.GetMessage()})");
                                break;
                            }
                        }

                        changes.Merge(resolver.Resolve(model, file.NewLines, context.Name, cancellationToken));

                        if (context.Descriptor.HasSourceGenerators)
                        {
                            changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
                                $"{context.Name} runs source generators; generated trees have no file on disk to attribute a change to");
                        }
                    }
                }
            }

            // Old side: deletions and renames are invisible in the new tree, and a deleted
            // override or interface implementation changes behaviour without touching any caller.
            var oldPath = file.OldSidePath;
            if (oldPath is null)
            {
                continue;
            }

            var oldContent = git.ShowFile(diff.BaseCommit, oldPath);
            if (oldContent is null)
            {
                continue;
            }

            var oldOwner = FindOwningProject(workspace, Path.GetFullPath(Path.Combine(options.RepositoryRoot, oldPath)))
                           ?? (documentId is not null
                               ? workspace.Projects.FirstOrDefault(p => p.Project.Id == workspace.Solution.GetDocument(documentId)!.Project.Id)?.Descriptor
                               : null);

            if (oldOwner is null)
            {
                continue;
            }

            var ownerContext = workspace.Projects.First(p => p.Name == oldOwner.Name);
            if (!typeIndexes.TryGetValue(oldOwner.Name, out var index))
            {
                index = SourceTypeIndex.Build(ownerContext.Compilation, cancellationToken);
                typeIndexes[oldOwner.Name] = index;
            }

            changes.Merge(new OldSideResolver(index).Resolve(oldContent, file.OldLines, oldOwner.Name, oldPath, cancellationToken));
        }

        return changes;
    }

    /// <summary>
    /// Reflection makes the call graph incomplete, so any project holding a reflecting file that
    /// is changed or impacted is widened rather than trusted.
    /// </summary>
    private void ApplyReflectionWidening(
        LoadedWorkspace workspace,
        ImpactGraph graph,
        DiffResult diff,
        ImpactTraversal traversal,
        SymbolChangeSet changes,
        CancellationToken cancellationToken)
    {
        var treesByPath = new Dictionary<string, (SyntaxTree Tree, string Project)>(PathComparer);
        foreach (var context in workspace.Projects)
        {
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                if (tree.FilePath.Length > 0)
                {
                    treesByPath.TryAdd(tree.FilePath, (tree, context.Name));
                }
            }
        }

        var candidates = new HashSet<string>(PathComparer);

        foreach (var file in diff.Files.Where(f => f.IsCSharp && f.ExistsOnNewSide))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(options.RepositoryRoot, file.Path)));
        }

        // Note that this walks the *impacted* nodes, not just the changed ones: reflection
        // anywhere along the path from a change to a test is what breaks the reasoning.
        foreach (var key in traversal.Impacted)
        {
            if (graph.TryGetNode(key)?.FilePath is { Length: > 0 } path)
            {
                candidates.Add(path);
            }
        }

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!treesByPath.TryGetValue(path, out var entry))
            {
                continue;
            }

            var findings = ReflectionScanner.Scan(entry.Tree, cancellationToken);
            if (findings.Count == 0)
            {
                continue;
            }

            changes.AddProjectWide(entry.Project, ProjectWideCause.Reflection,
                $"{Path.GetFileName(path)} uses {findings[0]}{(findings.Count > 1 ? $" and {findings.Count - 1} more" : string.Empty)}");
        }
    }

    // ---------------------------------------------------------------- selection

    private static IReadOnlySet<string> ExpandToDependents(
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
        var queue = new Queue<string>(projectWide.Select(c => c.ProjectName).Distinct(StringComparer.Ordinal));

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

    private static List<TestMethod> SelectTests(
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

    // ---------------------------------------------------------------- reporting

    private AnalysisReport BuildSelectiveReport(
        DiffResult diff,
        string? headCommit,
        IReadOnlyList<ProjectDescriptor> descriptors,
        IReadOnlyList<TestMethod> allTests,
        IReadOnlyList<TestMethod> selected,
        SymbolChangeSet changes,
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
            BaseRef = options.BaseRef,
            BaseCommit = diff.BaseCommit,
            HeadCommit = headCommit,
            Widenings = widenings,
            Diff = new DiffSummary
            {
                FileCount = diff.Files.Count,
                CSharpFileCount = diff.Files.Count(f => f.IsCSharp),
                ChangedSymbolCount = changes.Keys.Count,
                Files = [.. diff.Files.Select(f => f.ToString())],
            },
            Graph = graphSummary,
            TotalTests = allTests.Count,
            SelectedTests = projects.Sum(p => p.SelectedTests),
            Projects = projects,
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        };
    }

    private AnalysisReport FullRunReport(
        IReadOnlyList<string> reasons,
        IReadOnlyList<ProjectDescriptor> descriptors,
        IReadOnlyList<TestMethod> allTests,
        Stopwatch stopwatch,
        GraphSummary? graphSummary = null,
        DiffResult? diff = null,
        string? headCommit = null)
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
            TotalTests = allTests.Count,
            SelectedTests = allTests.Count,
            Projects = projects,
            ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        };
    }

    // ---------------------------------------------------------------- helpers

    private static ProjectDescriptor? FindOwningProject(LoadedWorkspace workspace, string absolutePath)
    {
        ProjectDescriptor? best = null;
        var bestLength = -1;

        foreach (var context in workspace.Projects)
        {
            var directory = context.Descriptor.Directory;
            if (directory.Length <= bestLength)
            {
                continue;
            }

            if (absolutePath.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison))
            {
                best = context.Descriptor;
                bestLength = directory.Length;
            }
        }

        return best;
    }

    private static bool PathsEqual(string a, string b) => string.Equals(a, b, PathComparison);

    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static StringComparer PathComparer =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
