using System.Runtime.InteropServices;
using Tia.Core.Caching;

namespace Tia.Integration.Tests;

/// <summary>
/// When the graph cache is written, and when it is left alone.
/// </summary>
/// <remarks>
/// It used to be rewritten on every run: a serialisation of every project and a several-hundred-
/// kilobyte write recording nothing that changed.
///
/// That cost is the whole objection. This comment used to claim a larger one - that a run killed
/// mid-write would destroy a known-good cache - and that was simply wrong: <c>GraphCache.Save</c>
/// has written through a temporary file and moved it into place since the first commit, so the
/// worst a killed run leaves behind is a stray <c>.tmp</c>.
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
