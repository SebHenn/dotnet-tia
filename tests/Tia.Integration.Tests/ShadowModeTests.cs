using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks;
using Tia.Workspace.Harness;

namespace Tia.Integration.Tests;

/// <summary>
/// Shadow mode: run everything, then report which failures a selection would have skipped.
/// </summary>
/// <remarks>
/// The mutation gate proves the engine cannot miss a fault it injected itself, into C#, in a
/// repository that was green to begin with. Shadow mode asks the same question of a real diff on
/// somebody else's code, which is the only way an application with container or HTTP dispatch finds
/// out whether selection is safe for it - and <c>docs/benchmarks.md</c> documents exactly such a
/// gap that no gate here could measure.
/// </remarks>
public sealed class ShadowModeTests
{
    [Fact]
    public void A_failing_test_that_was_selected_is_not_a_miss()
    {
        var misses = ShadowRunner.FindMisses(
            [("App.Tests", "App.Tests.WidgetTests.Adds")],
            [Filtered("App.Tests", "App.Tests.WidgetTests.Adds")]);

        Assert.Empty(misses);
    }

    [Fact]
    public void A_failing_test_that_was_not_selected_is_a_miss()
    {
        var misses = ShadowRunner.FindMisses(
            [("App.Tests", "App.Tests.WidgetTests.Removes")],
            [Filtered("App.Tests", "App.Tests.WidgetTests.Adds")]);

        Assert.Equal(["App.Tests.WidgetTests.Removes"], misses);
    }

    [Fact]
    public void A_project_running_unfiltered_cannot_contribute_a_miss()
    {
        // It runs every test it has, so nothing in it was at risk. Not an optimisation: a selection
        // covering most of a project deliberately drops the filter, and counting those failures
        // would report a miss for every test in a project the tool had decided to run whole.
        var misses = ShadowRunner.FindMisses(
            [("App.Tests", "App.Tests.WidgetTests.Removes")],
            [Unfiltered("App.Tests")]);

        Assert.Empty(misses);
    }

    [Fact]
    public void A_data_case_is_matched_against_the_method_it_belongs_to()
    {
        // Parameterised tests are selected whole, so the selection holds the method while the runner
        // reports the case. Disagreeing here would report a miss on every theory that failed.
        var misses = ShadowRunner.FindMisses(
            [("App.Tests", """App.Tests.WidgetTests.Cases(n: 3, name: "a")""")],
            [Filtered("App.Tests", "App.Tests.WidgetTests.Cases")]);

        Assert.Empty(misses);
    }

    [Fact]
    public void A_failure_in_one_project_is_not_excused_by_another_running_unfiltered()
    {
        var misses = ShadowRunner.FindMisses(
            [("App.Tests", "App.Tests.WidgetTests.Removes")],
            [Unfiltered("Other.Tests"), Filtered("App.Tests", "App.Tests.WidgetTests.Adds")]);

        Assert.Equal(["App.Tests.WidgetTests.Removes"], misses);
    }

    [Theory]
    [InlineData(DotnetTestMode.MicrosoftTestingPlatform, nameof(TestRunner.MicrosoftTestingPlatform), "--project", "--report-trx")]
    [InlineData(DotnetTestMode.VsTest, nameof(TestRunner.MicrosoftTestingPlatform), "--", "--report-trx")]
    [InlineData(DotnetTestMode.VsTest, nameof(TestRunner.VsTest), "--logger", "trx")]
    public void Every_runner_shape_is_asked_for_trx(
        DotnetTestMode mode, string runner, string expectedFirst, string expectedSecond)
    {
        // TRX is the one format both runners emit, and it is how shadow mode and the mutation gate
        // learn what failed. Get the shape wrong and no report is written at all - which reads as
        // "could not observe" rather than as a crash, so it fails quietly.
        var project = Filtered("App.Tests", "App.Tests.WidgetTests.Adds") with { Runner = runner };

        var arguments = SuiteRunner.Arguments(project, mode, "/tmp/results");

        Assert.Contains(expectedFirst, arguments);
        Assert.Contains(expectedSecond, arguments);
        Assert.Contains("/tmp/results", arguments);
    }

    private static ProjectSelection Filtered(string name, params string[] tests) => new()
    {
        Name = name,
        ProjectPath = name + "/" + name + ".csproj",
        Framework = nameof(TestFramework.XUnitV3),
        Runner = nameof(TestRunner.MicrosoftTestingPlatform),
        TotalTests = 10,
        SelectedTests = tests.Length,
        Filtered = true,
        Tests = tests,
    };

    private static ProjectSelection Unfiltered(string name) => new()
    {
        Name = name,
        ProjectPath = name + "/" + name + ".csproj",
        Framework = nameof(TestFramework.XUnitV3),
        Runner = nameof(TestRunner.MicrosoftTestingPlatform),
        TotalTests = 10,
        SelectedTests = 10,
        Filtered = false,
        Tests = [],
    };
}
