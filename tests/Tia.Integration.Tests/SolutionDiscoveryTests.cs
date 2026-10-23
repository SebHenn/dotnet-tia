using System.CommandLine;
using Tia.Cli;
using Tia.Core.Infrastructure;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// Which solution is analysed when none was named.
/// </summary>
/// <remarks>
/// <para>
/// Issue #37: with two solutions of the same kind at the root, discovery sorted them and took the
/// first, and said so only under <c>--verbose</c>. A selection computed on the wrong solution misses
/// every test project that only the other one contains, and nothing downstream can notice - from
/// inside the analysis, the solution it was handed is the repository.
/// </para>
/// <para>
/// The pattern order is not part of that and is pinned unchanged: a <c>.slnx</c> next to the
/// <c>.sln</c> it replaces is a migration in progress, and preferring the newer one is a choice the
/// layout makes rather than a guess.
/// </para>
/// </remarks>
public sealed class SolutionDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        FixtureRepository.RealTempDirectory(), "tia-discovery-" + Guid.NewGuid().ToString("n"));

    public SolutionDiscoveryTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => TemporaryDirectory.Delete(_directory);

    [Fact]
    public void Nothing_to_analyse_is_null()
    {
        Assert.Null(WorkspaceLoader.FindSolutionOrProject(_directory));
    }

    [Fact]
    public void A_single_solution_is_found()
    {
        var solution = Touch("App.sln");

        Assert.Equal(solution, WorkspaceLoader.FindSolutionOrProject(_directory));
    }

    /// <summary>The issue itself: two candidates, each named, and the one option that settles it.</summary>
    [Fact]
    public void Two_solutions_of_one_kind_are_refused_and_both_are_named()
    {
        Touch("Zeta.sln");
        Touch("Alpha.sln");

        var refusal = Assert.Throws<AmbiguousSolutionException>(() => WorkspaceLoader.FindSolutionOrProject(_directory));

        Assert.Equal(["Alpha.sln", "Zeta.sln"], refusal.Candidates);
        Assert.Contains("Alpha.sln, Zeta.sln", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("--solution", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slnx_beside_the_sln_it_replaces_still_wins()
    {
        var slnx = Touch("App.slnx");
        Touch("App.sln");

        Assert.Equal(slnx, WorkspaceLoader.FindSolutionOrProject(_directory));
    }

    /// <summary>
    /// Only the pattern that decides is asked whether it is ambiguous. Two <c>.sln</c> files that a
    /// single <c>.slnx</c> outranks were never going to be chosen.
    /// </summary>
    [Fact]
    public void Ambiguity_below_the_pattern_that_decides_is_not_ambiguity()
    {
        var slnx = Touch("App.slnx");
        Touch("Alpha.sln");
        Touch("Zeta.sln");

        Assert.Equal(slnx, WorkspaceLoader.FindSolutionOrProject(_directory));
    }

    /// <summary>And the converse: a lower-priority match is not a way out of a tie above it.</summary>
    [Fact]
    public void A_tie_in_the_deciding_pattern_does_not_fall_through_to_the_next()
    {
        Touch("Alpha.slnx");
        Touch("Zeta.slnx");
        Touch("Only.sln");

        var refusal = Assert.Throws<AmbiguousSolutionException>(() => WorkspaceLoader.FindSolutionOrProject(_directory));

        Assert.Equal(["Alpha.slnx", "Zeta.slnx"], refusal.Candidates);
    }

    [Fact]
    public void A_lone_project_is_found_when_there_is_no_solution()
    {
        var project = Touch("App.csproj");

        Assert.Equal(project, WorkspaceLoader.FindSolutionOrProject(_directory));
    }

    [Theory]
    [InlineData("App.slnx", "App.slnx")]
    [InlineData("src/NodaTime.slnx", "src/NodaTime.slnx")]
    [InlineData("../elsewhere/App.sln", "../elsewhere/App.sln")]
    public void The_solution_is_reported_relative_to_the_root_with_forward_slashes(string relative, string expected)
    {
        var root = Path.Combine(_directory, "repo");
        var solution = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal(expected, SelectionReporter.DescribeSolution(root, solution));
    }

    [Fact]
    public void No_solution_is_reported_as_none()
    {
        Assert.Null(SelectionReporter.DescribeSolution(_directory, null));
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}

/// <summary>
/// The refusal, end to end through every command that analyses.
/// </summary>
/// <remarks>
/// <para>
/// The commands reach discovery by different roads, and before this each road ended somewhere
/// different. The analyzer's fall-back-to-a-full-run caught the old "no solution" exception, so
/// <c>analyze</c> printed a full run naming no project and exited 0, <c>run</c> refused with 1, and
/// <c>watch</c> - which discovers for itself - went on to load whichever file sorted first.
/// </para>
/// <para>
/// None of this needs MSBuild: the refusal comes before any workspace is opened, which is also why
/// these are cheap enough to run per command.
/// </para>
/// </remarks>
public sealed class AmbiguousSolutionCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        FixtureRepository.RealTempDirectory(), "tia-ambiguous-" + Guid.NewGuid().ToString("n"));

    public AmbiguousSolutionCommandTests()
    {
        WorkspaceLoader.RegisterMSBuild();

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Alpha.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "Zeta.sln"), string.Empty);

        // Committed, so the tree is clean: `verify` refuses a dirty one first, and the refusal
        // under test would then never be reached.
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "tia@example.invalid");
        Git("config", "user.name", "tia tests");
        Git("add", "-A");
        Git("commit", "-m", "two solutions");
    }

    public void Dispose() => TemporaryDirectory.Delete(_root);

    /// <summary>
    /// The library half. Falling back is right for a failure inside an analysis and wrong here: the
    /// report it produced named no project, and `analyze` printed it and exited 0.
    /// </summary>
    [Fact]
    public async Task The_analyzer_does_not_fall_back_to_a_full_run()
    {
        var analyzer = new SolutionAnalyzer(new AnalysisOptions
        {
            RepositoryRoot = _root,
            BaseRef = "HEAD",
            UseCache = false,
            FallbackFullOnError = true,
        });

        await Assert.ThrowsAsync<AmbiguousSolutionException>(() => analyzer.AnalyzeAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("analyze", "--base", "HEAD")]
    [InlineData("analyze", "--base", "HEAD", "--json")]
    [InlineData("graph")]
    [InlineData("run", "--base", "HEAD")]
    [InlineData("run", "--base", "HEAD", "--dry-run")]
    [InlineData("explain", "SomeTest", "--base", "HEAD")]
    [InlineData("shadow", "--base", "HEAD")]
    [InlineData("verify", "--mutate", "1")]
    [InlineData("watch", "--base", "HEAD", "--once")]
    public async Task Every_command_refuses_with_exit_2(params string[] arguments)
    {
        var exit = await Program.BuildRoot(new CommonOptions())
            .Parse([.. arguments, "--path", _root, "--no-cache"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Inconclusive, exit);
    }

    /// <summary>
    /// `run` discovers before its build now, and a `--path` that does not exist must still reach
    /// the analysis, which refuses it with a sentence - not end as a stack trace from the search.
    /// The default exception handler is off so that a crash fails this test instead of becoming
    /// the same exit 1 the refusal returns.
    /// </summary>
    [Fact]
    public async Task Run_on_a_missing_path_refuses_rather_than_crashing()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var exit = await Program.BuildRoot(new CommonOptions())
            .Parse(["run", "--base", "HEAD", "--dry-run", "--path", missing, "--no-cache"])
            .InvokeAsync(
                new InvocationConfiguration { EnableDefaultExceptionHandler = false },
                TestContext.Current.CancellationToken);

        Assert.NotEqual(ExitCodes.Ok, exit);
    }

    /// <summary>Naming one is the way out, and the report then says which.</summary>
    [Fact]
    public async Task Naming_the_solution_settles_it()
    {
        var analyzer = new SolutionAnalyzer(new AnalysisOptions
        {
            RepositoryRoot = _root,
            BaseRef = "HEAD",
            SolutionPath = Path.Combine(_root, "Zeta.sln"),
            UseCache = false,
            FallbackFullOnError = true,
        });

        var outcome = await analyzer.AnalyzeAsync(TestContext.Current.CancellationToken);

        // An empty file is not a loadable solution, so this is a fall-back - which is the point: it
        // got far enough to fall back from, and the report names the file it fell back from.
        Assert.Equal("Zeta.sln", outcome.Report.Solution);
    }

    private void Git(params string[] args)
    {
        var result = ProcessRunner.Run("git", args, _root);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
        }
    }
}

/// <summary>Removal of a scratch directory that may hold git's read-only object files.</summary>
internal static class TemporaryDirectory
{
    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
