using System.Globalization;
using Tia.Cli;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary>
/// What the tool actually prints.
/// </summary>
/// <remarks>
/// <c>Render</c> is a pure <c>AnalysisReport -&gt; string</c> function with no MSBuild, no git and
/// no workspace behind it, so the layer that had no tests at all turns out to be the cheapest in
/// the codebase to cover. These assert the lines a reader makes decisions from: whether a full run
/// announced itself, whether the engine's precision is being confused with what will execute, and
/// whether the break-even is stated or quietly omitted.
/// </remarks>
public sealed class ReportRendererTests
{
    [Fact]
    public void A_full_run_announces_itself_and_says_why()
    {
        var output = ReportRenderer.Render(
            Report("full", reasons: ["src/App/App.csproj changed"]), verbose: false);

        Assert.Contains("FULL RUN - selection was not applied", output, StringComparison.Ordinal);
        Assert.Contains("src/App/App.csproj changed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_selective_run_does_not_claim_to_be_a_full_run()
    {
        var output = ReportRenderer.Render(Report("selective"), verbose: false);

        Assert.DoesNotContain("FULL RUN", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Impacted_and_selected_are_both_shown_when_they_differ()
    {
        // A project that runs unfiltered runs everything in it, so what the graph chose and what
        // will execute are different numbers. Printing only one flatters or maligns the engine.
        var output = ReportRenderer.Render(
            Report("selective", total: 100, impacted: 10, selected: 40), verbose: false);

        Assert.Contains("Impacted tests", output, StringComparison.Ordinal);
        Assert.Contains("Will run", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_impacted_is_shown_when_they_agree()
    {
        var output = ReportRenderer.Render(
            Report("selective", total: 100, impacted: 10, selected: 10), verbose: false);

        Assert.DoesNotContain("Will run", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_widening_is_printed()
    {
        // Conservatism that is not reported is indistinguishable from a bug.
        var report = Report("selective") with
        {
            Widenings = [new WideningEvent("Reflection", "App.Core", "Factory.cs uses Activator.CreateInstance")],
        };

        var output = ReportRenderer.Render(report, verbose: false);

        Assert.Contains("Reflection", output, StringComparison.Ordinal);
        Assert.Contains("Factory.cs uses Activator.CreateInstance", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_break_even_is_stated_when_selection_saves_anything()
    {
        var output = ReportRenderer.Render(
            Report("selective", total: 100, impacted: 10, selected: 10, elapsed: 9), verbose: false);

        Assert.Contains("Worth it if", output, StringComparison.Ordinal);
        Assert.DoesNotContain("never", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_selection_covering_everything_says_it_is_never_worth_it()
    {
        // Selecting the whole suite can never beat running the whole suite, and saying so is more
        // use than printing a threshold no suite could exceed.
        var output = ReportRenderer.Render(
            Report("selective", total: 100, impacted: 100, selected: 100, elapsed: 9), verbose: false);

        Assert.Contains("never - this diff selects the whole suite", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Selected_tests_are_listed_only_when_verbose()
    {
        var report = Report("selective") with
        {
            Projects =
            [
                new ProjectSelection
                {
                    Name = "App.Tests",
                    ProjectPath = "/repo/tests/App.Tests/App.Tests.csproj",
                    Framework = "XUnitV3",
                    Runner = "MicrosoftTestingPlatform",
                    TotalTests = 10,
                    SelectedTests = 1,
                    Filtered = true,
                    Tests = ["App.Tests.WidgetTests.Works"],
                },
            ],
        };

        Assert.DoesNotContain("App.Tests.WidgetTests.Works", ReportRenderer.Render(report, verbose: false), StringComparison.Ordinal);
        Assert.Contains("App.Tests.WidgetTests.Works", ReportRenderer.Render(report, verbose: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Output_does_not_move_with_the_locale()
    {
        // The renderer pinned InvariantCulture for counts but not for percentages, elapsed time or
        // durations, so on a decimal-comma machine one line read "1,204 of 3,730 (8,2 %)" - two
        // conventions in the same sentence. Tool output is read across machines and by other
        // programs; it should be the same everywhere.
        var report = Report("selective", total: 3730, impacted: 304, selected: 304, elapsed: 8.42);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = ReportRenderer.Render(report, verbose: false);

            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var german = ReportRenderer.Render(report, verbose: false);

            Assert.Equal(invariant, german);
            Assert.Contains("8.2 %", invariant, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static AnalysisReport Report(
        string mode,
        int total = 10,
        int impacted = 1,
        int selected = 1,
        double elapsed = 1.0,
        IReadOnlyList<string>? reasons = null) => new()
    {
        Mode = mode,
        BaseRef = "origin/main",
        BaseCommit = "73be09d0abcdef",
        FullRunReasons = reasons ?? [],
        TotalTests = total,
        ImpactedTests = impacted,
        SelectedTests = selected,
        ElapsedSeconds = elapsed,
    };
}
