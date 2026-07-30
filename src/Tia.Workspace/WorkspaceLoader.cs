using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Tia.Core.Model;
using Tia.Frameworks;

namespace Tia.Workspace;

/// <summary>A source-generated document that is part of the analysed compilation.</summary>
public sealed record GeneratedDocument(string HintName, SyntaxTree Tree);

public sealed record ProjectContext
{
    public required Project Project { get; init; }

    public required Compilation Compilation { get; init; }

    public required ProjectDescriptor Descriptor { get; init; }

    /// <summary>
    /// Syntax trees produced by source generators that are part of <see cref="Compilation"/>.
    /// When generated code is in the compilation the graph already covers it, so a change to
    /// generator input can be scoped to what actually depends on the generated code instead of
    /// widening the whole project.
    /// </summary>
    public IReadOnlyList<GeneratedDocument> GeneratedDocuments { get; init; } = [];

    public string Name => Descriptor.Name;
}

public sealed class LoadedWorkspace(MSBuildWorkspace workspace, IReadOnlyList<ProjectContext> projects, IReadOnlyList<string> failures)
    : IDisposable
{
    public IReadOnlyList<ProjectContext> Projects { get; } = projects;

    /// <summary>
    /// Workspace diagnostics of <see cref="WorkspaceDiagnosticKind.Failure"/> severity. A project
    /// that did not load is a project whose tests cannot be reasoned about, so these force a full
    /// run rather than being logged and ignored.
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
    /// Registers the SDK's MSBuild. This has to happen before any type that references MSBuild is
    /// JIT-loaded, which is why it is a separate no-inlining call made from the entry point rather
    /// than something the loader does lazily.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RegisterMSBuild()
    {
        if (_registered || MSBuildLocator.IsRegistered)
        {
            _registered = true;
            return;
        }

        foreach (var variable in InheritedMSBuildVariables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        // Leaving worker nodes alive after a short-lived analysis costs hundreds of megabytes
        // each and buys nothing: the process is about to exit.
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");

        MSBuildLocator.RegisterDefaults();
        _registered = true;
    }

    /// <summary>Opens a solution or project and produces a compilation for every C# project.</summary>
    public static async Task<LoadedWorkspace> LoadAsync(
        string path,
        string repositoryRoot,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        RegisterMSBuild();

        var failures = new List<string>();
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
                lock (failures)
                {
                    failures.Add(message);
                }
            }
            else
            {
                log?.Invoke(message);
            }
        });

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

        var contexts = new List<ProjectContext>();

        foreach (var project in workspace.CurrentSolution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (project.Language != LanguageNames.CSharp || project.FilePath is null)
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                failures.Add($"no compilation could be produced for {project.Name}");
                continue;
            }

            var generated = await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
            var generatedTrees = new List<GeneratedDocument>();

            foreach (var document in generated)
            {
                var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                if (tree is not null && compilation.ContainsSyntaxTree(tree))
                {
                    generatedTrees.Add(new GeneratedDocument(document.HintName, tree));
                }
            }

            contexts.Add(new ProjectContext
            {
                Project = project,
                Compilation = compilation,
                GeneratedDocuments = generatedTrees,
                Descriptor = Describe(project, compilation, repositoryRoot, generated.Any()),
            });
        }

        return new LoadedWorkspace(workspace, contexts, failures);
    }

    private static ProjectDescriptor Describe(
        Project project,
        Compilation compilation,
        string repositoryRoot,
        bool hasGenerators)
    {
        var referencedAssemblies = compilation.ReferencedAssemblyNames.Select(n => n.Name).ToList();
        var properties = MsBuildPropertyProbe.Read(project.FilePath!, repositoryRoot);

        var framework = FrameworkDetector.DetectFramework(referencedAssemblies);
        var runner = framework == TestFramework.Unknown
            ? TestRunner.Unknown
            : FrameworkDetector.DetectRunner(framework, referencedAssemblies, properties);

        var isTestProject = framework != TestFramework.Unknown ||
                            (properties.TryGetValue("IsTestProject", out var flag) && flag.Equals("true", StringComparison.OrdinalIgnoreCase));

        // Note this is not "does the project reference a generator" - every SDK project references
        // several (the interop and configuration-binding generators ship in the targeting pack),
        // and they emit nothing unless used. Widening on that would put every project in the
        // solution into full scope and destroy selection entirely. What matters is whether
        // generators actually produced code.

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
            HasSourceGenerators = hasGenerators,
        };
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
