using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// The graph on its own, for callers that ask what a solution uses rather than what a diff reaches.
/// </summary>
[Collection(nameof(FixtureCollection))]
public sealed class SolutionGraphTests(XunitFixtureRepository repository) : IDisposable
{
    public void Dispose() => repository.Revert();

    /// <summary>
    /// The analyzer reads a changed project file as a reason to run everything, in the same list
    /// as a project that did not load. Here there is no diff, so an edit in progress is not a
    /// problem with the load.
    /// </summary>
    [Fact]
    public async Task A_working_tree_with_uncommitted_changes_loads_without_problems()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");
        repository.Edit("Fixtures.Core/Fixtures.Core.csproj", "</Project>", "<!-- touched -->\n</Project>");

        var cacheBefore = CacheFiles();

        var graph = await SolutionGraph.LoadAsync(repository.AnalysisRoot, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(graph.Problems);
        Assert.Contains(graph.Tests, test => test.FullyQualifiedName == "Fixtures.Tests.CalculatorTests.Adds");
        Assert.Contains(graph.Projects, project => project.Name == "Fixtures.Core");

        // Other tests in this collection analyse with the cache on, so the directory may be there
        // already. What must not happen is this load adding to it or rewriting it.
        Assert.Equal(cacheBefore, CacheFiles());
    }

    private string[] CacheFiles()
    {
        var directory = repository.PathOf(".tia");
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{file}|{File.GetLastWriteTimeUtc(file):O}"))
                .Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// A project that does not compile has references the graph does not hold, and a caller that
    /// was not told would report everything only that project uses as unused.
    /// </summary>
    [Fact]
    public async Task A_project_that_does_not_compile_is_a_problem_naming_it()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a + ;");

        var graph = await SolutionGraph.LoadAsync(repository.AnalysisRoot, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(graph.Problems, problem => problem.StartsWith("Fixtures.Core/Calculator.cs does not compile", StringComparison.Ordinal));
    }
}
