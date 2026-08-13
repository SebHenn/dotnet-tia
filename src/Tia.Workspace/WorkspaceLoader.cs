using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks;

namespace Tia.Workspace;

/// <summary>A source-generated document that is part of the analysed compilation.</summary>
public sealed record GeneratedDocument(string HintName, SyntaxTree Tree);

/// <param name="EmittedCount">
/// How many documents the generators produced. Zero means no generator in this project emits
/// anything - which is the common case, since every SDK project references several that stay
/// silent unless used - and there is then nothing to widen or seed for.
/// </param>
public sealed record GeneratedOutput(int EmittedCount, IReadOnlyList<GeneratedDocument> InCompilation);

public sealed record ProjectContext
{
    private readonly Lazy<Compilation> _compilation = null!;

    public required Project Project { get; init; }

    /// <summary>
    /// Materialised on first use. Producing a compilation means parsing every document in the
    /// project, which on a solution of any size is most of the load cost - and when the cached
    /// graph fragment is still valid, nobody ever asks for it.
    /// </summary>
    public required Lazy<Compilation> LazyCompilation
    {
        get => _compilation;
        init => _compilation = value;
    }

    public Compilation Compilation => _compilation.Value;

    public bool IsCompiled => _compilation.IsValueCreated;

    public required ProjectDescriptor Descriptor { get; init; }

    /// <summary>
    /// Syntax trees produced by source generators that are part of <see cref="Compilation"/>.
    /// When generated code is in the compilation the graph already covers it, so a change to
    /// generator input can be scoped to what actually depends on the generated code instead of
    /// widening the whole project.
    /// </summary>
    public required Lazy<GeneratedOutput> LazyGeneratedOutput { get; init; }

    public GeneratedOutput GeneratedOutput => LazyGeneratedOutput.Value;

    public IReadOnlyList<GeneratedDocument> GeneratedDocuments => GeneratedOutput.InCompilation;

    public string Name => Descriptor.Name;
}

/// <summary>
/// A project the workspace listed but this engine cannot analyse - anything whose language is not
/// C#. It contributes no symbols, so nothing downstream can connect a change in it to a test.
/// </summary>
/// <remarks>
/// Kept out of <see cref="LoadedWorkspace.Projects"/> deliberately: everything there is expected to
/// produce a compilation. What these are for is attribution. A changed <c>.vb</c> file that belongs
/// to no C# project used to find no owner at all, so it widened nothing and selected nothing - a C#
/// test project exercising a VB library ran zero tests for a change to that library. Recording the
/// project lets the change widen it and, through the ordinary dependent expansion, everything that
/// references it.
/// </remarks>
public sealed record ForeignProject(string Name, string Language, string FilePath, string Directory);

public sealed class LoadedWorkspace(
    MSBuildWorkspace workspace,
    IReadOnlyList<ProjectContext> projects,
    IReadOnlyList<string> failures,
    IReadOnlyList<ForeignProject> foreignProjects)
    : IDisposable
{
    public IReadOnlyList<ProjectContext> Projects { get; } = projects;

    /// <summary>Projects the workspace listed in a language this engine does not analyse.</summary>
    public IReadOnlyList<ForeignProject> ForeignProjects { get; } = foreignProjects;

    /// <summary>
    /// Diagnostics that mean a project is actually missing, rather than merely complained about. A
    /// project that did not load is a project whose tests cannot be reasoned about, so these force
    /// a full run rather than being logged and ignored.
    /// </summary>
    public IReadOnlyList<string> Failures { get; } = failures;

    public Solution Solution => workspace.CurrentSolution;

    public void Dispose() => workspace.Dispose();
}

public static class WorkspaceLoader
{
    private static bool _registered;

    /// <summary>
    /// Environment variables MSBuild sets for the processes it launches. If the tool is running
    /// inside an MSBuild context - from an <c>Exec</c> task, a custom target, or a test host that
    /// <c>dotnet test</c> started - inheriting these points MSBuildLocator at the *host's* MSBuild
    /// rather than the SDK's. Project loads still succeed, so this shows up as an enormous
    /// slowdown rather than an error: the integration suite went from 26 seconds to 15 minutes.
    /// </summary>
    private static readonly string[] InheritedMSBuildVariables =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildSDKsPath",
        "MSBuildToolsPath",
        "MSBuildLoadMicrosoftTargetsReadOnly",
    ];

    /// <summary>
    /// Why MSBuild could not be registered, or null when it was. Set once, on the first attempt.
    /// </summary>
    public static string? RegistrationFailure { get; private set; }

    /// <summary>
    /// The MSBuild that was registered, or null when registration failed or has not been tried.
    /// Reported whichever way it went: which SDK is driving the load is the first thing worth
    /// knowing when a project will not open, and on a machine with several installed it is not
    /// something the caller can infer from the command line they typed.
    /// </summary>
    public static string? RegisteredMSBuild { get; private set; }

    /// <summary>The registered MSBuild's version, used to name it in an SDK-mismatch reason.</summary>
    internal static Version? RegisteredVersion { get; private set; }

    /// <summary>
    /// Registers the SDK's MSBuild, and reports whether it worked. This has to happen before any
    /// type that references MSBuild is JIT-loaded, which is why it is a separate no-inlining call
    /// made from the entry point rather than something the loader does lazily.
    /// </summary>
    /// <remarks>
    /// It fails on a machine that has the .NET *runtime* and no SDK - which is a perfectly normal
    /// target for <c>dotnet tool install -g</c>, and the machine most likely to run
    /// <c>dotnet-tia --help</c> first. Unguarded, that printed a raw stack trace from the first
    /// statement of Main before any command had been parsed. Failing here is not fatal on its own:
    /// help, version and usage errors need no MSBuild, so the entry point carries on and the
    /// commands that do need it say what is missing.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool RegisterMSBuild()
    {
        if (_registered || MSBuildLocator.IsRegistered)
        {
            _registered = true;
            return true;
        }

        if (RegistrationFailure is not null)
        {
            return false;
        }

        foreach (var variable in InheritedMSBuildVariables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        // Leaving worker nodes alive after a short-lived analysis costs hundreds of megabytes
        // each and buys nothing: the process is about to exit.
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");

        try
        {
            var instance = MSBuildLocator.RegisterDefaults();
            RegisteredVersion = instance.Version;
            RegisteredMSBuild = $"{instance.Name} {instance.Version} ({instance.MSBuildPath})";
        }
        catch (Exception ex)
        {
            RegistrationFailure =
                "No .NET SDK was found. dotnet tia reads projects through MSBuild, so it needs the " +
                "SDK installed and on PATH - the runtime alone is not enough. " +
                $"({ex.GetType().Name}: {ex.Message})";
            return false;
        }

        _registered = true;
        return true;
    }

    /// <summary>Opens a solution or project and produces a compilation for every C# project.</summary>
    public static async Task<LoadedWorkspace> LoadAsync(
        string path,
        string repositoryRoot,
        Action<string>? log = null,
        PhaseClock? clock = null,
        CancellationToken cancellationToken = default)
    {
        if (!RegisterMSBuild())
        {
            throw new InvalidOperationException(RegistrationFailure);
        }

        var diagnostics = new List<string>();
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            // Without this a single unloadable project type (a .esproj, a shared project) aborts
            // the whole load.
            ["SkipUnrecognizedProjects"] = "true",
        });

        workspace.SkipUnrecognizedProjects = true;
        using var failureSubscription = workspace.RegisterWorkspaceFailedHandler(args =>
        {
            var diagnostic = args.Diagnostic;
            var message = $"{diagnostic.Kind}: {diagnostic.Message}";
            if (diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                lock (diagnostics)
                {
                    diagnostics.Add(message);
                }
            }
            else
            {
                log?.Invoke(message);
            }
        });

        var openStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".sln" or ".slnx" or ".slnf")
        {
            await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (extension.EndsWith("proj", StringComparison.Ordinal))
        {
            await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException($"'{path}' is neither a solution nor a project file.");
        }

        clock?.Record(nameof(PhaseTimings.SolutionOpenSeconds), openStarted);

        var contexts = new List<ProjectContext>();
        var foreign = new List<ForeignProject>();

        // One collection for the whole load, so a project referenced by several others is evaluated
        // once. Disposed with the workspace: an undisposed ProjectCollection keeps every evaluated
        // project alive, which on a large solution is not a small amount of memory.
        using var propertyCollection = new Microsoft.Build.Evaluation.ProjectCollection();

        foreach (var project in workspace.CurrentSolution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (project.Language != LanguageNames.CSharp || project.FilePath is null)
            {
                // Skipping it is right - there is no C# compilation to be had - but forgetting it
                // is not. Without a record, a change to one of its files finds no owning project,
                // widens nothing, and quietly selects nothing at all.
                if (project.FilePath is { } foreignPath)
                {
                    foreign.Add(new ForeignProject(
                        project.Name,
                        project.Language,
                        foreignPath,
                        Path.GetDirectoryName(Path.GetFullPath(foreignPath))!));
                }

                continue;
            }

            var captured = project;

            var lazyCompilation = new Lazy<Compilation>(() =>
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = captured.GetCompilationAsync(CancellationToken.None).GetAwaiter().GetResult()
                             ?? throw new InvalidOperationException($"no compilation could be produced for {captured.Name}");
                clock?.Record(nameof(PhaseTimings.CompilationCpuSeconds), started);
                return result;
            });

            var lazyGenerated = new Lazy<GeneratedOutput>(() =>
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                // AsTask, not GetAwaiter().GetResult() directly: this returns a ValueTask, and
                // blocking on one that has not completed is undefined - it may be backed by a
                // pooled source that is recycled out from under the wait. It happens to work
                // today; it is not guaranteed to.
                var documents = captured.GetSourceGeneratedDocumentsAsync(CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().ToList();
                var trees = new List<GeneratedDocument>();

                foreach (var document in documents)
                {
                    var tree = document.GetSyntaxTreeAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (tree is not null && lazyCompilation.Value.ContainsSyntaxTree(tree))
                    {
                        trees.Add(new GeneratedDocument(document.HintName, tree));
                    }
                }

                clock?.Record(nameof(PhaseTimings.GeneratorProbeSeconds), started);
                return new GeneratedOutput(documents.Count, trees);
            });

            contexts.Add(new ProjectContext
            {
                Project = project,
                LazyCompilation = lazyCompilation,
                LazyGeneratedOutput = lazyGenerated,
                Descriptor = Describe(project, repositoryRoot, propertyCollection, clock),
            });
        }

        var loaded = contexts.Select(c => c.Descriptor).ToList();
        return new LoadedWorkspace(workspace, contexts, ReadFailures(diagnostics, loaded, foreign, log), foreign);
    }

    /// <summary>
    /// Separates the diagnostics that mean a project is missing from the ones that only mean
    /// MSBuild had something to say about a project that loaded anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MSBuildWorkspace"/> raises <see cref="WorkspaceDiagnosticKind.Failure"/> for
    /// anything MSBuild logged during the design-time build, warnings included, wrapped in the same
    /// "failed when processing the file" sentence either way. Treating every one of them as a load
    /// failure meant an ordinary NuGet warning forced a full run: on MediatR, whose test project
    /// multi-targets net462 and pulls two packages that say so, *every* analysis selected the whole
    /// suite and every mutation sample was unusable.
    /// </para>
    /// <para>
    /// So the diagnostic is evidence and the loaded solution is the verdict. A complaint naming a
    /// project that is present is logged; a complaint naming nothing that loaded is still a project
    /// whose tests cannot be reasoned about, and still forces a full run.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ReadFailures(
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<ProjectDescriptor> loaded,
        IReadOnlyList<ForeignProject> foreign,
        Action<string>? log = null)
    {
        var loadedPaths = loaded.Select(d => d.FilePath)
            .Concat(foreign.Select(f => f.FilePath))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failures = new List<string>();

        foreach (var diagnostic in diagnostics)
        {
            if (loadedPaths.Any(p => diagnostic.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                // Still printed, because MSBuild objecting is worth knowing - but prefixed, since a
                // bare line beginning "Failure:" on a run that succeeded reads as a broken tool.
                log?.Invoke($"the project loaded anyway - {diagnostic}");
                continue;
            }

            failures.Add(diagnostic);
        }

        // The hole the paragraph above opens, closed separately. A multi-targeted project becomes
        // one loaded project per framework, so it can arrive for one and not another - and the
        // diagnostic saying so names a path that did load, which the rule above would forgive. A
        // test project short of a framework is a set of tests nothing can see, which is a miss.
        // Only counted where the count is known: an unevaluated project declares nothing here.
        foreach (var group in loaded.Where(d => d.IsTestProject).GroupBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var declared = group.First().DeclaredTargetFrameworks;
            if (declared.Count > 0 && group.Count() < declared.Count)
            {
                failures.Add(
                    $"{Path.GetFileName(group.Key)} declares {declared.Count} target framework(s) " +
                    $"({string.Join(", ", declared)}) and loaded for {group.Count()}, so the tests of the " +
                    "rest cannot be reasoned about");
            }
        }

        return failures;
    }

    /// <summary>
    /// Describes a project without compiling it. Metadata reference file names carry the same
    /// information as the compilation's referenced assembly names for this purpose - which
    /// framework and runner the project uses - and reading them costs nothing.
    /// </summary>
    private static ProjectDescriptor Describe(
        Project project,
        string repositoryRoot,
        Microsoft.Build.Evaluation.ProjectCollection propertyCollection,
        PhaseClock? clock)
    {
        var referencedAssemblies = project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath ?? r.Display ?? string.Empty))
            .Where(n => n.Length > 0)
            .ToList();

        var framework = FrameworkDetector.DetectFramework(referencedAssemblies);

        // Evaluation is a second full MSBuild pass over the project, and measured at 1.8s across
        // this repository's seven projects - a third of the whole workspace load. The only answer
        // it changes is which runner a *test* project uses, so it is spent only where it buys
        // something. A project the referenced-assembly signal does not recognise as a test project
        // consults properties for one thing, its IsTestProject flag, and that is a literal in the
        // project file wherever it is set at all - which the XML read already sees.
        var probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var probed = framework == TestFramework.Unknown
            ? MsBuildPropertyProbe.Read(project.FilePath!, repositoryRoot)
            : MsBuildPropertyProbe.Read(project.FilePath!, repositoryRoot, propertyCollection);
        clock?.Record(nameof(PhaseTimings.PropertyEvaluationSeconds), probeStarted);

        var properties = probed.Values;

        var runner = framework == TestFramework.Unknown
            ? TestRunner.Unknown
            : FrameworkDetector.DetectRunner(framework, referencedAssemblies, properties);

        var isTestProject = framework != TestFramework.Unknown ||
                            (properties.TryGetValue("IsTestProject", out var flag) && flag.Equals("true", StringComparison.OrdinalIgnoreCase));

        // Whether generators actually emit anything is only knowable by running them, which is
        // deferred: it is asked exactly when a project has changed files, and never otherwise.

        return new ProjectDescriptor
        {
            Name = project.Name,
            AssemblyName = project.AssemblyName,
            FilePath = project.FilePath!,
            OutputFilePath = project.OutputFilePath,
            ProjectReferences = [.. ResolveProjectReferenceNames(project)],
            IsTestProject = isTestProject,
            Framework = framework,
            Runner = runner,
            PropertiesEvaluated = probed.Evaluated,
            DeclaredTargetFrameworks = probed.Evaluated ? DeclaredFrameworks(properties) : [],
        };
    }

    /// <summary>
    /// The declared framework list, from whichever of the two properties the project sets.
    /// <c>TargetFrameworks</c> wins: the SDK sets <c>TargetFramework</c> on each inner build, so a
    /// multi-targeted project evaluated for one framework reports both, and only the plural one
    /// says how many there are meant to be.
    /// </summary>
    private static IReadOnlyList<string> DeclaredFrameworks(IReadOnlyDictionary<string, string> properties)
    {
        if (properties.TryGetValue("TargetFrameworks", out var many) && many.Length > 0)
        {
            return [.. many.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        return properties.TryGetValue("TargetFramework", out var one) && one.Length > 0 ? [one] : [];
    }

    private static IEnumerable<string> ResolveProjectReferenceNames(Project project)
    {
        var solution = project.Solution;
        foreach (var reference in project.ProjectReferences)
        {
            var referenced = solution.GetProject(reference.ProjectId);
            if (referenced is not null)
            {
                yield return referenced.Name;
            }
        }
    }

    /// <summary>Finds the solution or project to analyse when none was named explicitly.</summary>
    public static string? FindSolutionOrProject(string directory)
    {
        foreach (var pattern in new[] { "*.slnx", "*.sln", "*.slnf" })
        {
            var matches = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            if (matches.Length == 1)
            {
                return matches[0];
            }

            if (matches.Length > 1)
            {
                Array.Sort(matches, StringComparer.Ordinal);
                return matches[0];
            }
        }

        var projects = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
        return projects.Length == 1 ? projects[0] : null;
    }
}
