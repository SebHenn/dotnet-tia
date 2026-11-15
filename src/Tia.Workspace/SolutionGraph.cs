using Tia.Core.Model;

namespace Tia.Workspace;

/// <summary>
/// A solution's reference graph and nothing else: no diff, no selection, no git.
/// </summary>
/// <remarks>
/// <see cref="SolutionAnalyzer"/> builds the same graph on its way to a selection, and reports what
/// went wrong with the load in the same list as what the diff forces - a changed project file, a
/// base that does not resolve. A caller that only wants the graph cannot tell the two apart, and
/// should not need a repository to ask.
/// </remarks>
/// <param name="Problems">
/// Why the graph is not the whole solution, or empty when it is: a project that did not load, one
/// that does not compile, a test project that resolved no tests, a project in a language the engine
/// does not read. Each one is references the graph does not hold.
/// </param>
public sealed record SolutionGraph(
    ImpactGraph Graph,
    IReadOnlyList<TestMethod> Tests,
    IReadOnlyList<ProjectContext> Projects,
    IReadOnlyList<string> Problems)
{
    /// <param name="directory">The directory the solution is discovered in, and that paths are relative to.</param>
    /// <param name="solutionPath">The solution or project to load, or null to discover one.</param>
    /// <exception cref="SetupException">Nothing to load was found.</exception>
    /// <exception cref="AmbiguousSolutionException">Discovery found several and none was named.</exception>
    public static async Task<SolutionGraph> LoadAsync(
        string directory,
        string? solutionPath = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        log ??= _ => { };
        var path = SetupException.RequireSolution(directory, solutionPath);
        var clock = new PhaseClock();

        using var workspace = await WorkspaceLoader.LoadAsync(path, directory, log, clock, cancellationToken).ConfigureAwait(false);

        // Never cached. The caller may be pointed at a repository it has no business writing
        // into, and the cache file is keyed for an analysis this is not.
        var options = new AnalysisOptions { RepositoryRoot = directory, BaseRef = string.Empty, UseCache = false };
        var built = new GraphBuilder(options, log, clock).Build(workspace, path, cancellationToken);

        List<string> problems =
        [
            .. workspace.Failures.Select(failure =>
                SdkMismatch.Describe(failure, WorkspaceLoader.RegisteredVersion) ?? $"a project failed to load: {failure}"),
            .. built.CompileErrors,

            // The project-level verdict reads declarations only, because that is all a selection
            // needs to trust the graph's shape. An error inside a body leaves the declarations
            // whole and the body half bound, and what that body refers to is missing from the graph.
            .. built.Fragments.Values
                .SelectMany(fragment => fragment.FileErrors)
                .Select(error => $"{Path.GetRelativePath(directory, error.FilePath).Replace('\\', '/')} does not compile ({error.Error})")
                .Order(StringComparer.Ordinal),
            .. built.LoadFailures,
            .. workspace.ForeignProjects.Select(project =>
                $"{project.Name} is {project.Language}, which is not analysed, so nothing it references is in the graph"),
        ];

        // The compilations outlive the workspace they were loaded through: they are immutable and
        // hold their own syntax trees and references.
        return new SolutionGraph(built.Graph, built.Tests, [.. workspace.Projects], problems);
    }
}
