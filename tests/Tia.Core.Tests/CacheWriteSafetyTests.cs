using System.Text.Json;
using Tia.Core.Caching;
using Tia.Core.Diff;
using Tia.Core.Reporting;

namespace Tia.Core.Tests;

/// <summary>
/// The small follow-ups of #44 that are about files: how the cache and the ledger are written,
/// where a custom cache directory is left out, and the version every JSON document carries.
/// </summary>
public sealed class CacheWriteSafetyTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tia-writes-");

    public void Dispose() => _directory.Delete(recursive: true);

    /// <summary>
    /// `watch` and a `run` beside it can save the same cache at once. With one shared temporary
    /// name the two interleaved into a file neither wrote; each now moves a whole file into place.
    /// </summary>
    [Fact]
    public void Concurrent_saves_of_one_cache_leave_a_loadable_file_and_no_temporaries()
    {
        var path = Path.Combine(_directory.FullName, "graph-test.bin");

        Parallel.For(0, 16, _ => GraphCache.Empty("sdk-1").Save(path));

        Assert.NotNull(GraphCache.TryLoad(path, "sdk-1"));
        Assert.Empty(Directory.GetFiles(_directory.FullName, "*.tmp"));
    }

    /// <summary>
    /// The ledger is rewritten to trim it, so it goes through a temporary file too; a kill
    /// mid-write used to be able to cut the history short.
    /// </summary>
    [Fact]
    public void Appending_to_the_ledger_keeps_every_record_and_leaves_no_temporary()
    {
        for (var i = 0; i < 3; i++)
        {
            RunLedger.Append(_directory.FullName, new RunRecord("2026-10-23T18:00:00Z", $"c{i}", false, 1, 2, 3, 4));
        }

        Assert.Equal(["c0", "c1", "c2"], RunLedger.Read(_directory.FullName).Select(r => r.HeadCommit));
        Assert.Empty(Directory.GetFiles(_directory.FullName, "*.tmp"));
    }

    [Theory]
    [InlineData("build/cache", "build/cache")]
    [InlineData(".tia", ".tia")]
    [InlineData(".", null)]
    [InlineData("..", null)]
    public void A_cache_directory_is_left_out_only_when_it_is_below_the_base(string cache, string? expected)
    {
        var root = Path.Combine(_directory.FullName, "repo");

        Assert.Equal(expected, CacheDirectory.RelativeTo(root, root, cache));
    }

    /// <summary>
    /// `.tia/` was never a change because its name was hardcoded, so a `--cache-dir` with any other
    /// name inside the repository showed up as one on every run (#44).
    /// </summary>
    [Fact]
    public void A_custom_cache_directory_is_not_a_change()
    {
        var git = new StubGit
        {
            NameStatusOutput = "A\0build/cache/graph-1.bin\0M\0src/App/Widget.cs\0",
            Untracked = ["build/cache/runs.jsonl", "build/cacheless/kept.txt"],
        };

        var diff = new DiffResolver(git, "build/cache").Resolve("main").Diff!;

        Assert.Equal(["src/App/Widget.cs", "build/cacheless/kept.txt"], diff.Files.Select(f => f.Path));
    }

    /// <summary>The first field of the report, so a consumer can check it before reading on.</summary>
    [Fact]
    public void The_report_json_opens_with_its_schema_version()
    {
        var report = new AnalysisReport { Mode = "full", BaseRef = "HEAD", TotalTests = 0, SelectedTests = 0 };

        using var document = JsonDocument.Parse(report.ToJson());
        var first = document.RootElement.EnumerateObject().First();

        Assert.Equal("schemaVersion", first.Name);
        Assert.Equal(AnalysisReport.SchemaVersion, first.Value.GetInt32());
    }

    private sealed class StubGit : IGitClient
    {
        public string RepositoryRoot => "/repo";

        public bool IsShallow => false;

        public string NameStatusOutput { get; init; } = string.Empty;

        public IReadOnlyList<string> Untracked { get; init; } = [];

        public string? ResolveCommit(string revision) => "base-commit";

        public string? MergeBase(string a, string b) => "base-commit";

        public bool IsAncestor(string ancestor, string descendant) => true;

        public string NameStatus(string baseCommit) => NameStatusOutput;

        public string Hunks(string baseCommit, IReadOnlyList<string> paths) => string.Empty;

        public IEnumerable<string> HunksInChunks(string baseCommit, IReadOnlyList<string> paths) => [string.Empty];

        public IReadOnlyList<string> UntrackedFiles() => Untracked;

        public string? ShowFile(string revision, string path) => null;

        public string? CurrentBranch() => "feature";

        public string? HeadCommit() => "head-commit";
    }
}
