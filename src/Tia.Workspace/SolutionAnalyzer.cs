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

    private readonly PhaseClock _clock = new();

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

        foreach (var failure in workspace.Failures)
        {
            earlyReasons.Add($"a project failed to load, so its tests cannot be reasoned about: {failure}");
        }

        // The graph is needed even for a full run: `graph` warms it, and reporting the totals is
        // what makes the selection ratio meaningful.
        var graphStarted = Stopwatch.GetTimestamp();
        var (graph, allTests, graphSummary, compileErrors, reflections) = BuildGraph(workspace, solutionPath, cancellationToken);
        _clock.Record(nameof(PhaseTimings.GraphSeconds), graphStarted);

        // A project that does not bind is a project whose tests cannot be reasoned about. The
        // verdict travels with the cached fragment, so an unchanged project is never re-checked.
        earlyReasons.AddRange(compileErrors);

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

        var traversal = _clock.Time(nameof(PhaseTimings.SelectionSeconds),
            () => ResolveReflection(graph, reflections, changes, cancellationToken));

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

    // ---------------------------------------------------------------- graph

    private (ImpactGraph Graph, IReadOnlyList<TestMethod> Tests, GraphSummary Summary, IReadOnlyList<string> CompileErrors,
        IReadOnlyList<(string Project, ReflectionRecord Record)> Reflections) BuildGraph(
        LoadedWorkspace workspace,
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var sdkVersion = RuntimeInformation.FrameworkDescription;
        var cachePath = Path.Combine(options.RepositoryRoot, options.CacheDirectory, GraphCache.FileName(solutionPath, sdkVersion));
        var cache = options.UseCache ? GraphCache.TryLoad(cachePath, sdkVersion) : null;
        var fresh = GraphCache.Empty(sdkVersion);

        // Assembly names come from the descriptor so that naming the tracked set does not force
        // every project to compile.
        var trackedAssemblies = new HashSet<string>(
            workspace.Projects.Select(p => p.Descriptor.AssemblyName),
            StringComparer.Ordinal);

        // Decide reuse before producing a single compilation. A project whose own content is
        // unchanged and whose dependencies' declarations are unchanged has a valid fragment, and
        // parsing it again would be the largest avoidable cost in the whole run.
        var references = workspace.Projects.ToDictionary(
            p => p.Name, p => p.Descriptor.ProjectReferences, StringComparer.Ordinal);

        var fingerprints = _clock.Time(nameof(PhaseTimings.FingerprintSeconds), () =>
        {
            var content = ProjectFingerprint.ComputeContentHashes(workspace.Projects, cancellationToken);
            var surface = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var context in workspace.Projects)
            {
                var cachedFragment = cache?.Projects.GetValueOrDefault(context.Name);

                // A project whose content is unchanged has an unchanged declaration surface too,
                // so the stored hash stands and the compilation is never needed.
                surface[context.Name] =
                    cachedFragment is not null && cachedFragment.ContentHash == content[context.Name]
                        ? cachedFragment.SurfaceHash
                        : ProjectFingerprint.ComputeSurface(context, cancellationToken);
            }

            return (Content: content, Surface: surface);
        });

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
            var fingerprint = ProjectFingerprint.Combine(
                context.Name, fingerprints.Content, fingerprints.Surface, references);

            if (cache is not null &&
                cache.Projects.TryGetValue(context.Name, out var cached) &&
                string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                fragments[index] = cached;
                Interlocked.Increment(ref reused);
                return;
            }

            _log($"rebuilding {context.Name}: " + (cache is null
                ? "no cache"
                : !cache.Projects.ContainsKey(context.Name)
                    ? "not in cache"
                    : cache.Projects[context.Name].ContentHash != fingerprints.Content[context.Name]
                        ? "own source changed"
                        : "a dependency's declarations changed"));

            var projectGraph = builder.Build(context.Compilation, context.Name, cancellationToken);
            var tests = context.Descriptor.IsTestProject
                ? discoverer.Discover(context.Compilation, context.Name, context.Descriptor.Framework, projectGraph, cancellationToken)
                : [];

            fragments[index] = new ProjectGraphFragment
            {
                ProjectName = context.Name,
                Fingerprint = fingerprint,
                ContentHash = fingerprints.Content[context.Name],
                SurfaceHash = fingerprints.Surface[context.Name],
                CompileError = FirstCompileError(context, cancellationToken),
                Reflections = ScanProjectForReflection(context, cancellationToken),
                Graph = projectGraph,
                Tests = tests,
            };

            Interlocked.Increment(ref rebuilt);
        });

        var graph = new ImpactGraph();
        var allTests = new List<TestMethod>();
        var compileErrors = new List<string>();
        var reflections = new List<(string Project, ReflectionRecord Record)>();

        foreach (var fragment in fragments)
        {
            if (fragment is null)
            {
                continue;
            }

            graph.Merge(fragment.Graph);
            allTests.AddRange(fragment.Tests);
            fresh.Projects[fragment.ProjectName] = fragment;
            reflections.AddRange(fragment.Reflections.Select(r => (fragment.ProjectName, r)));

            if (fragment.CompileError is { Length: > 0 } error)
            {
                compileErrors.Add($"{fragment.ProjectName} does not compile ({error})");
            }
        }

        compileErrors.Sort(StringComparer.Ordinal);

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

        return (graph, allTests, summary, compileErrors, reflections);
    }

    /// <summary>
    /// Every reflecting or serializing site in the project, found while its compilation is already
    /// in hand.
    /// </summary>
    /// <remarks>
    /// Scanning the whole project, rather than only the files a traversal reached, is what closes
    /// the miss class the NodaTime gate found. A reflecting member is dangerous precisely when
    /// nothing reaches it: NodaTime's <c>TestHelper.AssertXmlRoundtrip</c> hands a value to
    /// <c>XmlSerializer</c>, which calls the type's <c>ReadXml</c>, which parses with
    /// <c>InstantPattern</c> - so breaking the pattern parser failed every round-trip test, and the
    /// old scan never looked at the helper because no static path led to it. The reasoning it
    /// encoded, "reflection elsewhere only matters if the member holding it is in the impact set",
    /// is exactly backwards for dispatch that starts outside the impact set.
    /// </remarks>
    private static IReadOnlyList<ReflectionRecord> ScanProjectForReflection(ProjectContext context, CancellationToken cancellationToken)
    {
        var records = new List<ReflectionRecord>();

        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (tree.FilePath.Length == 0)
            {
                continue;
            }

            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var finding in ReflectionScanner.Scan(model, cancellationToken))
            {
                records.Add(new ReflectionRecord(finding.Description, finding.OwningMemberKey, tree.FilePath));
            }
        }

        return records;
    }

    /// <summary>
    /// The project's first declaration-level error, or null when it binds cleanly. Stored with the
    /// fragment: the same source bound against the same declarations yields the same verdict, so
    /// an unchanged project need not be re-checked.
    /// </summary>
    private static string? FirstCompileError(ProjectContext context, CancellationToken cancellationToken)
    {
        var error = context.Compilation
            .GetDeclarationDiagnostics(cancellationToken)
            .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);

        return error is null ? null : $"{error.Id}: {error.GetMessage()}";
    }

    // ---------------------------------------------------------------- changes

    /// <remarks>
    /// Diff paths are relative to the *git root*, which is not necessarily the directory being
    /// analysed: a solution can live in a subdirectory of its repository. Resolving them against
    /// the analysed directory instead produced paths that matched no document at all, so nothing
    /// was ever selected - a silent miss rather than an error.
    /// </remarks>
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

        // Collected as we go, then used once per project: recomputing generated output is a
        // per-project operation, not a per-file one.
        var changedByProject = new Dictionary<string, List<ChangedFile>>(StringComparer.Ordinal);
        var baseSourcesByProject = new Dictionary<string, List<BaseSource>>(StringComparer.Ordinal);

        foreach (var file in diff.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var absolute = Path.GetFullPath(Path.Combine(git.RepositoryRoot, file.Path));

            if (!file.IsCSharp)
            {
                // Recorded even when it does not widen: a generator may read it, which is what
                // makes recomputing that project's generated output unreliable.
                RecordProjectChange(workspace, absolute, file, null, changedByProject, baseSourcesByProject);

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

            // Fetched before the new side is read, because whether the new side is worth reading
            // at all depends on what the old side said.
            var oldPath = file.OldSidePath;
            var oldContent = oldPath is null ? null : git.ShowFile(diff.BaseCommit, oldPath);

            // New side. Every project the file belongs to, not just the first: a multi-targeted
            // project compiles the same file once per framework, with different preprocessor
            // symbols, so a change inside `#if NET8_0` is real code in one of them and disabled
            // text in the others. Reading only the first meant whichever framework the workspace
            // happened to list first decided whether the change existed at all.
            var triviaOnlyEverywhere = true;
            var sawAnyTree = false;
            var reportedCompileError = false;

            foreach (var documentId in file.ExistsOnNewSide
                         ? workspace.Solution.GetDocumentIdsWithFilePath(absolute)
                         : [])
            {
                var document = workspace.Solution.GetDocument(documentId)!;
                var context = workspace.Projects.FirstOrDefault(p => p.Project.Id == document.Project.Id);
                var tree = context?.Compilation.SyntaxTrees.FirstOrDefault(t => PathsEqual(t.FilePath, absolute));

                if (context is null || tree is null)
                {
                    continue;
                }

                sawAnyTree = true;

                if (IsTriviaOnly(context, file, oldContent, tree, cancellationToken))
                {
                    continue;
                }

                triviaOnlyEverywhere = false;

                var model = context.Compilation.GetSemanticModel(tree);

                foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken))
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        // Once per file, not once per target framework.
                        if (!reportedCompileError)
                        {
                            compilationErrors.Add($"{file.Path} does not compile ({diagnostic.Id}: {diagnostic.GetMessage()})");
                            reportedCompileError = true;
                        }

                        break;
                    }
                }

                changes.Merge(resolver.Resolve(model, file.NewLines, context.Name,
                    isNewFile: file.Kind == FileChangeKind.Added, cancellationToken));
            }

            // Only when every project that compiles the file agrees. One framework's disabled
            // region is another's live code.
            if (sawAnyTree && triviaOnlyEverywhere)
            {
                changes.Notes.Add($"{file.Path} changed only comments or formatting; no token moved, so it seeds nothing");
                continue;
            }

            // Old side: deletions and renames are invisible in the new tree, and a deleted
            // override or interface implementation changes behaviour without touching any caller.
            RecordProjectChange(workspace, absolute, file, oldContent, changedByProject, baseSourcesByProject);

            if (oldPath is null || oldContent is null)
            {
                continue;
            }

            // Falls back to whichever project holds the file on the new side. Any of them will do:
            // the old side is resolved against a type index, and a multi-targeted project's
            // frameworks declare the same types.
            var newSideOwner = workspace.Solution.GetDocumentIdsWithFilePath(absolute).FirstOrDefault();

            var oldOwner = FindOwningProject(workspace, Path.GetFullPath(Path.Combine(git.RepositoryRoot, oldPath)))
                           ?? (newSideOwner is not null
                               ? workspace.Projects.FirstOrDefault(p => p.Project.Id == workspace.Solution.GetDocument(newSideOwner)!.Project.Id)?.Descriptor
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

        foreach (var context in workspace.Projects)
        {
            if (changedByProject.ContainsKey(context.Name))
            {
                SeedGeneratedCode(
                    context,
                    changedByProject[context.Name],
                    baseSourcesByProject.GetValueOrDefault(context.Name, []),
                    resolver,
                    changes,
                    cancellationToken);
            }
        }

        return changes;
    }

    /// <summary>Files that changed in each project, with their base-revision content.</summary>
    private static void RecordProjectChange(
        LoadedWorkspace workspace,
        string absolutePath,
        ChangedFile file,
        string? baseContent,
        Dictionary<string, List<ChangedFile>> changedByProject,
        Dictionary<string, List<BaseSource>> baseSourcesByProject)
    {
        var owner = FindOwningProject(workspace, absolutePath);
        if (owner is null)
        {
            return;
        }

        if (!changedByProject.TryGetValue(owner.Name, out var files))
        {
            files = [];
            changedByProject[owner.Name] = files;
        }

        files.Add(file);

        if (!file.IsCSharp)
        {
            return;
        }

        if (!baseSourcesByProject.TryGetValue(owner.Name, out var sources))
        {
            sources = [];
            baseSourcesByProject[owner.Name] = sources;
        }

        sources.Add(new BaseSource(absolutePath, baseContent));
    }

    /// <summary>
    /// Handles a change to a project whose generators actually emit code.
    /// </summary>
    /// <remarks>
    /// The blunt rule is to widen the whole project, because generated trees have no file on disk
    /// to attribute a change to. That is enormously expensive - one generator in a core library
    /// puts the entire solution into full scope - and it is stronger than the risk requires.
    ///
    /// When the generated documents are part of the compilation being analysed, the graph already
    /// carries every edge out of them. The only thing a change to generator input can do that the
    /// graph cannot see is alter the generated code itself, so treating every generated document
    /// as changed models exactly that: whatever depends on generated code is selected, and
    /// whatever does not is left alone. The project-wide widening remains the fallback for when
    /// the generated documents are not in the compilation, where nothing else would be sound.
    /// </remarks>
    private void SeedGeneratedCode(
        ProjectContext context,
        IReadOnlyList<ChangedFile> changedFiles,
        IReadOnlyList<BaseSource> baseSources,
        ChangedSymbolResolver resolver,
        SymbolChangeSet changes,
        CancellationToken cancellationToken)
    {
        var generated = context.GeneratedOutput;

        if (generated.EmittedCount == 0)
        {
            // No generator in this project emits anything. Every SDK project references several
            // that stay silent unless used, so this is the ordinary case and it costs nothing.
            return;
        }

        if (generated.InCompilation.Count == 0)
        {
            changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
                $"{context.Name} runs source generators whose output is not in the analysed compilation, " +
                "so a change to their input cannot be attributed at symbol granularity");
            return;
        }

        // A generator may read an AdditionalFile, an embedded resource, anything. Only a diff made
        // entirely of C# can be replayed faithfully by substituting syntax trees.
        var comparison = changedFiles.All(f => f.IsCSharp)
            ? GeneratedCodeDiffer.Compare(context, baseSources, cancellationToken)
            : new GeneratedCodeComparison { Exact = false, Reason = "the diff touches files the generators may read" };

        var toSeed = comparison.Exact ? comparison.Changed : context.GeneratedDocuments;

        if (toSeed.Count == 0)
        {
            changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
                $"{context.Name} runs source generators; re-running them over both revisions shows no generated document changed",
                widensProject: false);
            return;
        }

        var before = changes.Keys.Count;

        foreach (var document in toSeed)
        {
            var model = context.Compilation.GetSemanticModel(document.Tree);
            changes.Merge(resolver.Resolve(model, [WholeFile], context.Name, isNewFile: false, cancellationToken));
        }

        changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
            comparison.Exact
                ? $"{context.Name}: {comparison.Reason}; {changes.Keys.Count - before} generated symbol(s) treated as changed"
                : $"{context.Name} runs source generators and {comparison.Reason}, so all " +
                  $"{toSeed.Count} generated document(s) are treated as changed",
            widensProject: false);
    }

    private static readonly LineRange WholeFile = new(1, int.MaxValue);

    /// <summary>
    /// Seeds every reflecting and serializing member, then traverses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection means a member can reach things no static edge records, so the strongest sound
    /// statement about it is that it is always impacted. Seeding the member says exactly that, and
    /// then the graph does the rest: a reflecting test selects itself, while a reflecting library
    /// method selects everything that reaches it - which is precisely the set at risk when, say, a
    /// registry discovers a newly added type by scanning the assembly. Widening the whole project
    /// instead selects the same members plus every unrelated test alongside them, and on a codebase
    /// whose test suite uses reflection at all that collapses selection to nearly 100%.
    /// </para>
    /// <para>
    /// "Always impacted" used to mean "if the traversal reaches it", which is not the same thing
    /// and is wrong in the case that matters. The NodaTime gate found it: nothing statically
    /// reaches <c>TestHelper.AssertXmlRoundtrip</c>, so it was never scanned, so the XmlSerializer
    /// call inside it was never noticed - and breaking the pattern parser its serialized types use
    /// failed sixty tests the selection had left out. A reflecting member is dangerous *because*
    /// nothing reaches it. Every one in the solution is a seed now, found once per project when
    /// its fragment is built and carried in the cache so a warm run pays nothing for it.
    /// </para>
    /// <para>
    /// Only when something else changed, though. With an empty change set there is nothing for a
    /// reflecting member to depend on, and a diff that touches nothing must still select nothing.
    /// </para>
    /// </remarks>
    private ImpactTraversal ResolveReflection(
        ImpactGraph graph,
        IReadOnlyList<(string Project, ReflectionRecord Record)> reflections,
        SymbolChangeSet changes,
        CancellationToken cancellationToken)
    {
        var selector = new ImpactSelector();

        if (changes.IsEmpty)
        {
            return selector.Traverse(graph, changes.Keys, cancellationToken);
        }

        foreach (var group in reflections.GroupBy(r => (r.Project, r.Record.FilePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var records = group.Select(g => g.Record).ToList();
            var file = Path.GetFileName(group.Key.FilePath);

            foreach (var record in records)
            {
                if (record.OwningMemberKey is { } owner)
                {
                    changes.Keys.Add(owner);
                }
                else
                {
                    // Reflection outside any member - a top-level statement, a field initialiser
                    // the model could not resolve - leaves nothing to seed, so the project-wide
                    // reading is the only sound fallback.
                    changes.AddProjectWide(group.Key.Project, ProjectWideCause.Reflection,
                        $"{file} uses {record.Description} outside any member");
                }
            }

            changes.AddProjectWide(group.Key.Project, ProjectWideCause.Reflection,
                $"{file} uses {records[0].Description}" +
                (records.Count > 1 ? $" and {records.Count - 1} more" : string.Empty) +
                "; the reflecting member(s) are treated as always impacted",
                widensProject: false);
        }

        return selector.Traverse(graph, changes.Keys, cancellationToken);
    }

    /// <summary>
    /// Whether this file's change moved no token, in which case it seeds nothing.
    /// </summary>
    /// <remarks>
    /// Only for a modified file: an add or a delete is not a formatting change however its tokens
    /// compare. And only when the project's generators emit nothing, because a generator reads the
    /// syntax tree including its trivia - an attribute's doc comment, a marker in a comment - so in
    /// a project with a live generator a comment really can change what is compiled.
    /// </remarks>
    private static bool IsTriviaOnly(
        ProjectContext context,
        ChangedFile file,
        string? oldContent,
        SyntaxTree tree,
        CancellationToken cancellationToken) =>
        oldContent is not null &&
        file.Kind == FileChangeKind.Modified &&
        context.GeneratedOutput.EmittedCount == 0 &&
        TriviaOnlyChange.Applies(oldContent, tree.GetText(cancellationToken).ToString(), tree.Options, cancellationToken);

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
            Timings = _clock.Snapshot(),
            TotalTests = allTests.Count,
            ImpactedTests = projects.Sum(p => p.Tests.Count),
            SelectedTests = projects.Sum(p => p.SelectedTests),
            Projects = projects,
            Diagnostics = [.. changes.Notes],
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
            Timings = _clock.Snapshot(),
            TotalTests = allTests.Count,
            ImpactedTests = allTests.Count,
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
