using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks;

namespace Tia.Core.Tests;

/// <summary>
/// The command the tool actually shells out to.
/// </summary>
/// <remarks>
/// Everything upstream can be right and still produce nothing useful here: the three
/// <c>dotnet test</c> shapes take the project and the filter in different places, so the wrong one
/// fails outright rather than running the wrong tests. It had no direct assertions, and the only
/// caller in the test suite - <c>FixtureRepository</c> - patches around its <c>--</c> placement to
/// append its own logger arguments, so even that call exercised part of it by accident.
/// </remarks>
public sealed class TestCommandBuilderTests
{
    [Fact]
    public void The_vstest_command_takes_the_project_positionally()
    {
        Assert.Equal(
            ["test", "/repo/App.Tests.csproj", "--filter", "A|B"],
            TestCommandBuilder.Build(Selection(TestRunner.VsTest), DotnetTestMode.VsTest, []));
    }

    [Fact]
    public void A_testing_platform_runner_on_the_vstest_command_gets_a_separator()
    {
        // The project is still positional, but the filter belongs to the test executable rather
        // than to `dotnet test`, so it has to cross `--` to get there.
        Assert.Equal(
            ["test", "/repo/App.Tests.csproj", "--", "--filter-query", "/*/*/*/A"],
            TestCommandBuilder.Build(
                Selection(TestRunner.MicrosoftTestingPlatform, ["--filter-query", "/*/*/*/A"]),
                DotnetTestMode.VsTest,
                []));
    }

    [Fact]
    public void The_platform_native_command_moves_the_project_and_drops_the_separator()
    {
        // Opted into repository-wide through global.json. Passing `--` here reaches the runner as
        // a literal argument and the run fails.
        Assert.Equal(
            ["test", "--project", "/repo/App.Tests.csproj", "--filter-query", "/*/*/*/A"],
            TestCommandBuilder.Build(
                Selection(TestRunner.MicrosoftTestingPlatform, ["--filter-query", "/*/*/*/A"]),
                DotnetTestMode.MicrosoftTestingPlatform,
                []));
    }

    [Fact]
    public void A_project_that_runs_unfiltered_is_invoked_with_no_filter()
    {
        // The filter arguments are still on the selection - the report prints what was computed
        // even when it was abandoned - so dropping them is this method's job, and emitting them
        // would silently reintroduce the filter the safety model just decided against.
        var project = Selection(TestRunner.VsTest) with { Filtered = false };

        Assert.Equal(
            ["test", "/repo/App.Tests.csproj"],
            TestCommandBuilder.Build(project, DotnetTestMode.VsTest, []));
    }

    [Fact]
    public void A_filtered_project_with_no_filter_arguments_gets_no_separator_either()
    {
        var project = Selection(TestRunner.MicrosoftTestingPlatform, []);

        Assert.Equal(
            ["test", "/repo/App.Tests.csproj"],
            TestCommandBuilder.Build(project, DotnetTestMode.VsTest, []));
    }

    [Fact]
    public void Passthrough_arguments_reach_dotnet_test_and_not_the_runner()
    {
        // `tia run -- --no-build` is an instruction to `dotnet test`. Emitting it after the
        // separator would hand it to the test executable, which does not know it.
        Assert.Equal(
            ["test", "/repo/App.Tests.csproj", "--no-build", "--", "--filter-query", "/*/*/*/A"],
            TestCommandBuilder.Build(
                Selection(TestRunner.MicrosoftTestingPlatform, ["--filter-query", "/*/*/*/A"]),
                DotnetTestMode.VsTest,
                ["--no-build"]));
    }

    /// <summary>
    /// A wave replaces the filter and nothing else. Where the arguments go is a property of the
    /// runner, and getting it wrong for the second invocation only would produce a run that
    /// half-works - the kind of defect that reads as a flaky suite.
    /// </summary>
    [Fact]
    public void A_wave_puts_its_own_filter_exactly_where_the_project_filter_would_have_gone()
    {
        var project = Selection(TestRunner.MicrosoftTestingPlatform, ["--filter-query", "/*/*/*/A|B"]);

        Assert.Equal(
            ["test", "/repo/App.Tests.csproj", "--", "--filter-query", "/*/*/*/A"],
            TestCommandBuilder.Build(project, DotnetTestMode.VsTest, [], ["--filter-query", "/*/*/*/A"]));

        Assert.Equal(
            ["test", "--project", "/repo/App.Tests.csproj", "--filter-query", "/*/*/*/B"],
            TestCommandBuilder.Build(
                project, DotnetTestMode.MicrosoftTestingPlatform, [], ["--filter-query", "/*/*/*/B"]));
    }

    [Fact]
    public void An_unrecognised_runner_is_treated_as_vstest()
    {
        // A project whose framework the discoverer could not identify still has to be invocable,
        // and the VSTest command is the one that works without opting into anything.
        var project = Selection(TestRunner.VsTest) with { Runner = "SomethingElse" };

        Assert.Equal(
            ["test", "/repo/App.Tests.csproj", "--filter", "A|B"],
            TestCommandBuilder.Build(project, DotnetTestMode.VsTest, []));
    }

    [Fact]
    public void An_unrecognised_mode_on_the_report_is_treated_as_vstest()
    {
        Assert.Equal(DotnetTestMode.VsTest, TestCommandBuilder.ModeOf(Report("nonsense")));
        Assert.Equal(DotnetTestMode.VsTest, TestCommandBuilder.ModeOf(Report(nameof(DotnetTestMode.VsTest))));
        Assert.Equal(
            DotnetTestMode.MicrosoftTestingPlatform,
            TestCommandBuilder.ModeOf(Report(nameof(DotnetTestMode.MicrosoftTestingPlatform))));
    }

    [Fact]
    public void The_printed_command_quotes_what_a_shell_would_split()
    {
        // This string is printed for the user to copy, and a VSTest filter is a pipe-separated
        // list - unquoted, the shell reads it as a pipeline and the command does something else
        // entirely.
        var printed = TestCommandBuilder.Describe(
            TestCommandBuilder.Build(Selection(TestRunner.VsTest), DotnetTestMode.VsTest, []));

        Assert.Equal("dotnet test /repo/App.Tests.csproj --filter \"A|B\"", printed);
    }

    [Fact]
    public void The_printed_command_quotes_a_path_with_a_space_in_it()
    {
        var project = Selection(TestRunner.VsTest) with { ProjectPath = "/repo/My Tests/App.Tests.csproj" };

        Assert.Contains(
            "\"/repo/My Tests/App.Tests.csproj\"",
            TestCommandBuilder.Describe(TestCommandBuilder.Build(project, DotnetTestMode.VsTest, [])),
            StringComparison.Ordinal);
    }

    private static ProjectSelection Selection(TestRunner runner, IReadOnlyList<string>? filter = null) => new()
    {
        Name = "App.Tests",
        ProjectPath = "/repo/App.Tests.csproj",
        Framework = nameof(TestFramework.XUnitV3),
        Runner = runner.ToString(),
        TotalTests = 10,
        SelectedTests = 2,
        Filtered = true,
        FilterArguments = filter ?? ["--filter", "A|B"],
    };

    private static AnalysisReport Report(string mode) => new()
    {
        Mode = "selective",
        BaseRef = "origin/main",
        BaseCommit = "73be09d0",
        TotalTests = 10,
        SelectedTests = 2,
        DotnetTestMode = mode,
    };
}
