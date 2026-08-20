using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Tia.Core.Analysis;
using Tia.Core.Caching;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Safety;
using Tia.Frameworks;

namespace Tia.Workspace;

/// <summary>Everything one pass over the workspace produces.</summary>
/// <param name="Fragments">
/// Every project's fragment as it stands after this run, reused or rebuilt alike. Change
/// resolution reads declaration positions, per-file compile verdicts and generator counts out of
/// these, which is what keeps it from having to produce a compilation.
/// </param>
internal sealed record BuiltGraph(
    ImpactGraph Graph,
    IReadOnlyList<TestMethod> Tests,
    GraphSummary Summary,
    IReadOnlyList<string> CompileErrors,
    IReadOnlyList<(string Project, ReflectionRecord Record)> Reflections,
    IReadOnlyList<RouteRecord> Routes,
    IReadOnlyList<TypeFlowFact> TypeFacts,
    IReadOnlyDictionary<string, ProjectGraphFragment> Fragments);

/// <summary>
/// Builds the reverse reference graph for a solution, reusing every cached fragment it can.
/// </summary>
/// <remarks>
/// The graph is built even when the answer is already known to be a full run: `graph` exists to
/// warm the cache, and reporting the totals is what makes a selection ratio mean anything. The
/// expensive half is parsing, so the order here matters - reuse is decided from fingerprints
/// before a single compilation is produced, and a project whose content and whose dependencies'
/// declarations are unchanged is never parsed at all.
/// </remarks>
internal sealed class GraphBuilder(AnalysisOptions options, Action<string> log, PhaseClock clock)
{
    private readonly Action<string> _log = log;

    private readonly PhaseClock _clock = clock;

    public BuiltGraph Build(
        LoadedWorkspace workspace,
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var sdkVersion = RuntimeInformation.FrameworkDescription;
        var cachePath = Path.Combine(
            options.RepositoryRoot,
            options.CacheDirectory,
            GraphCache.FileName(solutionPath, sdkVersion, options.TypeFlow));
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
            var surface = new string[workspace.Projects.Count];

            // Parallel for the same reason the rebuild below is. Re-hashing a surface means
            // producing the project's compilation, which is the second most expensive thing this
            // tool does; running those one after another left every core but one idle while a
            // multi-targeted core library was hashed twice in sequence.
            Parallel.For(0, workspace.Projects.Count, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            }, index =>
            {
                var context = workspace.Projects[index];
                var cachedFragment = cache?.Projects.GetValueOrDefault(context.Name);

                // A project whose content is unchanged has an unchanged declaration surface too,
                // so the stored hash stands and the compilation is never needed.
                surface[index] =
                    cachedFragment is not null && cachedFragment.ContentHash == content[context.Name]
                        ? cachedFragment.SurfaceHash
                        : _clock.Measure(
                            nameof(PhaseTimings.SurfaceHashCpuSeconds),
                            () => ProjectFingerprint.ComputeSurface(context, cancellationToken));
            });

            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < workspace.Projects.Count; index++)
            {
                byName[workspace.Projects[index].Name] = surface[index];
            }

            return (Content: content, Surface: byName);
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
                : !cache.Projects.TryGetValue(context.Name, out var previous)
                    ? "not in cache"
                    : previous.ContentHash != fingerprints.Content[context.Name]
                        ? "own source changed"
                        : "a dependency's declarations changed"));

            // Forcing the compilation is charged to CompilationCpuSeconds by the lazy itself, and
            // reading it here rather than inside each Measure below keeps that cost out of the
            // phases that follow it.
            var compilation = context.Compilation;

            var symbols = _clock.Measure(
                nameof(PhaseTimings.GraphWalkCpuSeconds),
                () => builder.BuildSymbols(compilation, context.Name, cancellationToken));
            var projectGraph = symbols.Graph;

            var tests = context.Descriptor.IsTestProject
                ? _clock.Measure(
                    nameof(PhaseTimings.TestDiscoveryCpuSeconds),
                    () => discoverer.Discover(compilation, context.Name, context.Descriptor.Framework, projectGraph, cancellationToken))
                : [];

            fragments[index] = new ProjectGraphFragment
            {
                ProjectName = context.Name,
                Fingerprint = fingerprint,
                ContentHash = fingerprints.Content[context.Name],
                SurfaceHash = fingerprints.Surface[context.Name],
                CompileError = FirstCompileError(context, cancellationToken),
                Reflections = _clock.Measure(
                    nameof(PhaseTimings.ReflectionScanCpuSeconds),
                    () => ScanProjectForReflection(context, cancellationToken)),
                Routes = _clock.Measure(
                    nameof(PhaseTimings.RouteScanCpuSeconds),
                    () => RouteTemplateIndex.Scan(compilation, context.Name, cancellationToken)),
                TypeFacts = options.TypeFlow
                    ? _clock.Measure(
                        nameof(PhaseTimings.TypeFlowScanCpuSeconds),
                        () => TypeFlow.Scan(compilation, trackedAssemblies, cancellationToken))
                    : [],
                Declarations = symbols.Declarations,
                FileErrors = _clock.Measure(
                    nameof(PhaseTimings.FileDiagnosticsCpuSeconds),
                    () => FileCompileErrors(compilation, cancellationToken)),

                // Asked here, where the compilation exists, rather than during change resolution,
                // where asking used to be what produced one.
                GeneratedDocumentCount = context.GeneratedOutput.EmittedCount,
                Graph = projectGraph,
                Tests = tests,
            };

            Interlocked.Increment(ref rebuilt);
        });

        var graph = new ImpactGraph();
        var allTests = new List<TestMethod>();
        var compileErrors = new List<string>();
        var reflections = new List<(string Project, ReflectionRecord Record)>();
        var routes = new List<RouteRecord>();
        var typeFacts = new List<TypeFlowFact>();

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
            routes.AddRange(fragment.Routes);
            typeFacts.AddRange(fragment.TypeFacts);

            if (fragment.CompileError is { Length: > 0 } error)
            {
                compileErrors.Add($"{fragment.ProjectName} does not compile ({error})");
            }
        }

        compileErrors.Sort(StringComparer.Ordinal);

        // Nothing was rebuilt, so `fresh` is the file that is already on disk, fragment for
        // fragment. Rewriting it costs a serialisation of every project and a several-hundred-
        // kilobyte write on a run that had nothing to record - and, worse, it puts a known-good
        // cache through a truncate-and-rewrite on every invocation. A run killed mid-write loses a
        // cache it had no reason to touch, which is the whole of this file's value.
        //
        // The reuse count is the right condition rather than `rebuilt == 0`: a project that has
        // disappeared from the solution leaves a fragment in the loaded cache that is no longer in
        // `fresh`, and that difference has to be written even though nothing was rebuilt.
        var unchanged = rebuilt == 0 && cache is not null && cache.Projects.Count == fresh.Projects.Count;

        if (options.UseCache && !unchanged)
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

        return new BuiltGraph(graph, allTests, summary, compileErrors, reflections, routes, typeFacts, fresh.Projects);
    }

    /// <summary>
    /// The first binding error in each file, or nothing when every body binds.
    /// </summary>
    /// <remarks>
    /// Collected here, once, while the compilation is in hand and its bodies are already bound by
    /// the graph walk. Change resolution used to ask the same question per changed file, which
    /// meant a semantic model, which meant a compilation - on a warm run that produced one for
    /// every project a diff touched and was a fifth of the run.
    /// </remarks>
    private static IReadOnlyList<FileCompileError> FileCompileErrors(Compilation compilation, CancellationToken cancellationToken)
    {
        var byFile = new Dictionary<string, FileCompileError>(StringComparer.OrdinalIgnoreCase);

        foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            // A diagnostic with no tree is about the compilation rather than a file - a missing
            // reference, a bad option - and the project-level verdict already covers those.
            if (diagnostic.Location.SourceTree is not { FilePath.Length: > 0 } tree)
            {
                continue;
            }

            byFile.TryAdd(tree.FilePath, new FileCompileError(
                tree.FilePath,
                $"{diagnostic.Id}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)}"));
        }

        return [.. byFile.Values];
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
    /// <summary>
    /// The frameworks a project declares, preferring what MSBuild computed and falling back to the
    /// literal read. The evaluated list is only populated for test projects - it is there to count
    /// how many of a suite's frameworks arrived - and the project that cannot resolve anything is
    /// usually a library, so the fallback is what answers here.
    /// </summary>
    private IReadOnlyList<string> DeclaredFrameworksOf(ProjectContext context)
    {
        if (context.Descriptor.DeclaredTargetFrameworks.Count > 0)
        {
            return context.Descriptor.DeclaredTargetFrameworks;
        }

        try
        {
            return WorkspaceLoader.DeclaredFrameworks(
                MsBuildPropertyProbe.Read(context.Descriptor.FilePath, options.RepositoryRoot).Values);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A project file that cannot be read is already being reported as not compiling. The
            // compiler's own message stands rather than this becoming a second failure.
            return [];
        }
    }

    private string? FirstCompileError(ProjectContext context, CancellationToken cancellationToken)
    {
        // Timed because it used to be the second-biggest phase and the report claimed otherwise:
        // CompileCheckSeconds was declared, serialised and never recorded, so --json emitted a
        // flat 0 for work that once took 1.3 seconds. A timing that is always zero is worse than
        // no timing - it says the phase is free.
        var started = Stopwatch.GetTimestamp();

        try
        {
            var error = context.Compilation
                .GetDeclarationDiagnostics(cancellationToken)
                .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);

            if (error is null)
            {
                return null;
            }

            // Only on the one error that means nothing resolved, so the extra project read costs
            // nothing on a run that is going to succeed. An SDK too old for the project does not
            // fail the load - it produces a project with no references - and saying "does not
            // compile" there blames the project for the toolchain.
            var mismatch = SdkMismatch.DescribeUnresolved(
                error.Id,
                DeclaredFrameworksOf(context),
                WorkspaceLoader.RegisteredVersion);

            return mismatch ?? $"{error.Id}: {error.GetMessage(CultureInfo.InvariantCulture)}";
        }
        finally
        {
            _clock.Record(nameof(PhaseTimings.CompileCheckCpuSeconds), started);
        }
    }
}
