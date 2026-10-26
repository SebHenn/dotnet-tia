using Tia.Cli;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// A command pointed at something it cannot analyse at all refuses with exit 2 (#50).
/// </summary>
/// <remarks>
/// <para>
/// Three setups: a directory that is not in a git repository, a repository with no solution or
/// project in it, and a <c>--path</c> that does not exist. Each used to throw inside the analysis,
/// where the full-run fallback caught it and produced a report naming no project. <c>analyze</c>
/// and <c>graph</c> printed that and exited 0, and <c>run</c> refused it with exit 1 as though a
/// full run had been computed and found empty. It is the shape #37 fixed for several solutions.
/// </para>
/// <para>
/// None of this needs MSBuild to load anything: every refusal comes before a workspace is opened.
/// </para>
/// </remarks>
public sealed class SetupErrorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        FixtureRepository.RealTempDirectory(), "tia-setup-" + Guid.NewGuid().ToString("n"));

    public SetupErrorTests()
    {
        WorkspaceLoader.RegisterMSBuild();
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TemporaryDirectory.Delete(_root);

    /// <summary>The directory a case analyses, set up the way the case says.</summary>
    private string Prepare(string setup)
    {
        switch (setup)
        {
            case "not-a-repository":
                File.WriteAllText(Path.Combine(_root, "App.sln"), string.Empty);
                return _root;

            case "no-solution":
                File.WriteAllText(Path.Combine(_root, "README.md"), "nothing to build");
                TestGit.Init(_root);
                TestGit.Run(_root, "add", "-A");
                TestGit.Run(_root, "commit", "-m", "no solution");
                return _root;

            default:
                return Path.Combine(_root, "does-not-exist");
        }
    }

    public static TheoryData<string, string[]> Cases()
    {
        string[][] commands =
        [
            ["analyze", "--base", "HEAD"],
            ["analyze", "--base", "HEAD", "--json"],
            ["graph"],
            ["run", "--base", "HEAD"],
            ["run", "--base", "HEAD", "--dry-run"],
            ["explain", "SomeTest", "--base", "HEAD"],
            ["shadow", "--base", "HEAD"],
            ["verify", "--mutate", "1"],
            ["watch", "--base", "HEAD", "--once"],
        ];

        var data = new TheoryData<string, string[]>();
        foreach (var setup in new[] { "not-a-repository", "no-solution", "missing-path" })
        {
            foreach (var command in commands)
            {
                data.Add(setup, command);
            }
        }

        return data;
    }

    /// <summary>
    /// The default exception handler is off, so a crash fails the test rather than becoming the
    /// exit 1 that the old refusal returned too.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Every_command_refuses_with_exit_2(string setup, string[] arguments)
    {
        var path = Prepare(setup);

        var exit = await Cli.InvokeUnhandledAsync([.. arguments, "--path", path, "--no-cache"]);

        Assert.Equal(ExitCodes.Inconclusive, exit);
    }

    /// <summary>
    /// The library half: with fallback on, as every command runs it, the analyzer still does not
    /// turn a setup error into a full run of nothing.
    /// </summary>
    [Theory]
    [InlineData("not-a-repository")]
    [InlineData("no-solution")]
    [InlineData("missing-path")]
    public async Task The_analyzer_does_not_fall_back_to_a_full_run(string setup)
    {
        var analyzer = new SolutionAnalyzer(new AnalysisOptions
        {
            RepositoryRoot = Prepare(setup),
            BaseRef = "HEAD",
            UseCache = false,
            FallbackFullOnError = true,
        });

        await Assert.ThrowsAsync<SetupException>(() => analyzer.AnalyzeAsync(TestContext.Current.CancellationToken));
    }
}
