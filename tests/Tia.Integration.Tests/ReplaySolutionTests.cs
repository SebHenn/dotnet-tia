using Tia.Workspace.Harness;

namespace Tia.Integration.Tests;

/// <summary>
/// Which solution <c>replay</c> walks at each commit (#139). NodaTime keeps its solution in
/// <c>src/</c>, and <c>replay -p .</c> skipped every commit with MSB1003 and no hint why.
/// </summary>
public sealed class ReplaySolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(FixtureRepository.RealTempDirectory(), "tia-replay-" + Guid.NewGuid().ToString("n"));

    public ReplaySolutionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_solution_at_the_path_is_used()
    {
        var solution = Touch("App.slnx");
        Touch("src/Other.slnx");

        Assert.Equal(solution, ReplayBenchmark.FindSolution(_root, named: null, out _));
    }

    [Fact]
    public void A_solution_one_level_below_the_path_is_found()
    {
        var solution = Touch("src/NodaTime.slnx");
        Touch("src/NodaTime/NodaTime.csproj");

        Assert.Equal(solution, ReplayBenchmark.FindSolution(_root, named: null, out _));
    }

    [Fact]
    public void Lone_projects_one_level_below_are_not_solutions()
    {
        // Every project folder holds one; taking any of them would replay an arbitrary project.
        Touch("Library/Library.csproj");

        Assert.Null(ReplayBenchmark.FindSolution(_root, named: null, out var reason));
        Assert.Contains("--solution", reason, StringComparison.Ordinal);
        Assert.Contains("--path", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_solutions_below_are_named_rather_than_guessed_between()
    {
        Touch("src/App.slnx");
        Touch("samples/Samples.sln");

        Assert.Null(ReplayBenchmark.FindSolution(_root, named: null, out var reason));
        Assert.Contains(Path.Combine("src", "App.slnx"), reason, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("samples", "Samples.sln"), reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_and_build_output_folders_are_not_searched()
    {
        Touch(".git/Stray.sln");
        Touch("bin/Stray.sln");
        var solution = Touch("src/App.slnx");

        Assert.Equal(solution, ReplayBenchmark.FindSolution(_root, named: null, out _));
    }

    [Fact]
    public void A_named_solution_missing_at_a_commit_is_a_reason_not_a_silent_skip()
    {
        var named = Path.Combine(_root, "src", "Renamed.slnx");
        Touch("src/App.slnx");

        Assert.Null(ReplayBenchmark.FindSolution(_root, named, out var reason));
        Assert.Contains("Renamed.slnx does not exist at this commit", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_solution_wins_over_discovery()
    {
        Touch("App.slnx");
        var named = Touch("src/Other.slnx");

        Assert.Equal(named, ReplayBenchmark.FindSolution(_root, named, out _));
    }

    [Fact]
    public void Merges_older_than_recent_history_suggest_first_parent()
    {
        var day = (long)TimeSpan.FromDays(1).TotalSeconds;
        var now = 1_800_000_000L;

        var warning = ReplayBenchmark.StaleMergeWarning(newestMerge: now - 900 * day, oldestRecent: now - 30 * day, recentCount: 20);

        Assert.NotNull(warning);
        Assert.Contains("--first-parent", warning, StringComparison.Ordinal);
        Assert.Null(ReplayBenchmark.StaleMergeWarning(newestMerge: now - day, oldestRecent: now - 30 * day, recentCount: 20));
    }

    private string Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
