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

    /// <summary>
    /// One instance of a multi-targeted project (#67). Without the framework, `dotnet test` runs
    /// every framework the project declares - once per instance.
    /// </summary>
    [Fact]
    public void An_instance_of_a_multi_targeted_project_runs_only_its_own_framework()
    {
        var project = Selection(TestRunner.MicrosoftTestingPlatform) with { TargetFramework = "net48" };

        Assert.Equal(
            ["test", "/repo/App.Tests.csproj", "--framework", "net48", "--no-build", "--", "--filter", "A|B"],
            TestCommandBuilder.Build(project, DotnetTestMode.VsTest, ["--no-build"]));
    }

    [Theory]
    [InlineData("--framework", "net10.0")]
    [InlineData("-f", "net10.0")]
    [InlineData("--framework=net10.0", null)]
    [InlineData("-f:net10.0", null)]
    public void A_framework_the_caller_chose_is_not_contradicted(string option, string? value)
    {
        var project = Selection(TestRunner.VsTest) with { TargetFramework = "net48" };
        IReadOnlyList<string> passthrough = value is null ? [option] : [option, value];

        var arguments = TestCommandBuilder.Build(project, DotnetTestMode.VsTest, passthrough);

        Assert.DoesNotContain("net48", arguments);
    }

    /// <summary>
    /// Instances that selected the same thing run as one invocation, which runs their frameworks
    /// side by side - the cheapest way to run them all, and what `dotnet test` does unasked.
    /// </summary>
    [Fact]
    public void Instances_that_selected_the_same_tests_run_as_one_invocation()
    {
        var net48 = Selection(TestRunner.VsTest) with { Name = "App.Tests(net48)", TargetFramework = "net48" };
        var net10 = Selection(TestRunner.VsTest) with { Name = "App.Tests(net10.0)", TargetFramework = "net10.0" };
        var other = Selection(TestRunner.VsTest) with { Name = "Other.Tests", ProjectPath = "/repo/Other.Tests.csproj" };

        var coalesced = TestCommandBuilder.Coalesce([net48, other, net10]);

        Assert.Equal(["App.Tests(net48)", "Other.Tests"], coalesced.Select(p => p.Name));
        Assert.Null(coalesced[0].TargetFramework);
        Assert.Equal(["test", "/repo/App.Tests.csproj", "--filter", "A|B"], TestCommandBuilder.Build(coalesced[0], DotnetTestMode.VsTest, []));
    }

    [Fact]
    public void Instances_that_selected_different_tests_each_keep_their_framework()
    {
        var net48 = Selection(TestRunner.VsTest, ["--filter", "A"]) with { Name = "App.Tests(net48)", TargetFramework = "net48" };
        var net10 = Selection(TestRunner.VsTest, ["--filter", "A|B"]) with { Name = "App.Tests(net10.0)", TargetFramework = "net10.0" };

        var coalesced = TestCommandBuilder.Coalesce([net48, net10]);

        Assert.Equal(["net48", "net10.0"], coalesced.Select(p => p.TargetFramework));
    }

    [Fact]
    public void Instances_that_differ_only_in_how_they_divide_into_waves_are_not_folded()
    {
        var wave = new RunWave { TestCount = 1, FilterArguments = ["--filter", "A"], RemainderFilterArguments = ["--filter", "B"] };
        var net48 = Selection(TestRunner.VsTest) with { Name = "App.Tests(net48)", TargetFramework = "net48", FirstWave = wave };
        var net10 = Selection(TestRunner.VsTest) with { Name = "App.Tests(net10.0)", TargetFramework = "net10.0" };

        Assert.Equal(2, TestCommandBuilder.Coalesce([net48, net10]).Count);
    }

    [Fact]
    public void A_vstest_trx_report_is_a_dotnet_test_option_before_any_separator()
    {
        var project = Selection(TestRunner.VsTest) with { WritesTrx = true, Filtered = false };

        var arguments = TestCommandBuilder.Build(project, DotnetTestMode.VsTest, ["--no-build", "--", "RunConfiguration.X=1"], [], "/tmp/r");

        Assert.Equal(
            ["test", "/repo/App.Tests.csproj", "--no-build", "--logger", "trx", "--results-directory", "/tmp/r", "--", "RunConfiguration.X=1"],
            arguments);
    }

    [Fact]
    public void A_testing_platform_trx_report_crosses_the_separator_on_the_vstest_command()
    {
        var project = Selection(TestRunner.MicrosoftTestingPlatform, ["--filter-method", "A"]) with { WritesTrx = true };

        var arguments = TestCommandBuilder.Build(project, DotnetTestMode.VsTest, [], project.FilterArguments, "/tmp/r");

        Assert.Equal(
            ["test", "/repo/App.Tests.csproj", "--", "--filter-method", "A", "--report-trx", "--results-directory", "/tmp/r"],
            arguments);
    }

    [Fact]
    public void An_unfiltered_testing_platform_project_gets_a_separator_for_its_report()
    {
        var project = Selection(TestRunner.MicrosoftTestingPlatform) with { WritesTrx = true, Filtered = false };

        var arguments = TestCommandBuilder.Build(project, DotnetTestMode.VsTest, [], [], "/tmp/r");

        Assert.Equal(["test", "/repo/App.Tests.csproj", "--", "--report-trx", "--results-directory", "/tmp/r"], arguments);
    }

    [Fact]
    public void The_platform_native_command_takes_the_report_directly()
    {
        var project = Selection(TestRunner.MicrosoftTestingPlatform, ["--filter-method", "A"]) with { WritesTrx = true };

        var arguments = TestCommandBuilder.Build(project, DotnetTestMode.MicrosoftTestingPlatform, [], project.FilterArguments, "/tmp/r");

        Assert.Equal(
            ["test", "--project", "/repo/App.Tests.csproj", "--filter-method", "A", "--report-trx", "--results-directory", "/tmp/r"],
            arguments);
    }

    [Fact]
    public void A_project_that_cannot_write_trx_is_not_asked_to()
    {
        // --report-trx without the extension is an unknown option, which fails the run.
        var project = Selection(TestRunner.MicrosoftTestingPlatform, ["--filter-method", "A"]) with { WritesTrx = false };

        var arguments = TestCommandBuilder.Build(project, DotnetTestMode.VsTest, [], project.FilterArguments, "/tmp/r");

        Assert.DoesNotContain("--report-trx", arguments);
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
