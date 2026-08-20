using System.Runtime.InteropServices;
using Tia.Core.Caching;

namespace Tia.Integration.Tests;

/// <summary>
/// When the graph cache is written, and when it is left alone.
/// </summary>
/// <remarks>
/// It used to be rewritten on every run. On a warm run that is a serialisation of every project
/// and a several-hundred-kilobyte write recording nothing that changed - but the cost is the
/// smaller half. The real objection is that it puts a known-good cache through a
/// truncate-and-rewrite on every invocation, so a run killed mid-write destroys a file it had no
/// reason to open. This tool's own harness has been killed mid-run more than once.
/// </remarks>
[Collection(nameof(FixtureCollection))]
public sealed class GraphCacheWriteTests(XunitFixtureRepository repository) : IDisposable
{
    public void Dispose()
    {
        repository.Revert();

        // This test's own file, not the directory. The fixture tree outlives a session, so `.tia`
        // also holds caches written by earlier runs under other SDK versions - globbing for
        // `graph-*.bin` found three of them and deleting the directory would throw away files that
        // are not this test's to remove.
        File.Delete(CacheFile());
    }

    /// <summary>
    /// The exact file this run would write, named the way <c>GraphBuilder</c> names it.
    /// </summary>
    private string CacheFile() => Path.Combine(
        repository.AnalysisRoot,
        ".tia",
        GraphCache.FileName(repository.SolutionPath, RuntimeInformation.FrameworkDescription));

    [Fact]
    public async Task A_run_that_rebuilt_nothing_leaves_the_cache_file_untouched()
    {
        var warm = await repository.AnalyzeAsync(useCache: true);
        Assert.True(warm.Graph.ProjectsRebuilt > 0, "the first run has nothing to reuse");

        var path = CacheFile();
        Assert.True(File.Exists(path), $"the first run should have written {path}");

        var writtenAt = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;

        var second = await repository.AnalyzeAsync(useCache: true);

        Assert.Equal(0, second.Graph.ProjectsRebuilt);
        Assert.True(second.Graph.ProjectsReused > 0);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(path));
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task A_run_that_rebuilt_something_writes_the_cache()
    {
        await repository.AnalyzeAsync(useCache: true);

        var path = CacheFile();
        var writtenAt = File.GetLastWriteTimeUtc(path);

        repository.Edit(
            "Fixtures.Core/Calculator.cs",
            "public int Add(int a, int b) => a + b;",
            "public int Add(int a, int b) => b + a;");

        var after = await repository.AnalyzeAsync(useCache: true);

        Assert.True(after.Graph.ProjectsRebuilt > 0);
        Assert.NotEqual(writtenAt, File.GetLastWriteTimeUtc(path));
    }
}
