using Tia.Cli.Commands;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary>
/// The decision `run` makes before invoking anything.
/// </summary>
/// <remarks>
/// This exists because of a defect that inverted the tool's whole safety promise. Analysis that
/// threw fell back to a full run, the fallback report carried no projects, `run` filtered for
/// projects with selected tests, found none, printed "no test was impacted by this diff" and
/// returned 0. A failed analysis ran zero tests and reported success - and blamed the diff for a
/// decision the failure had made.
/// </remarks>
public sealed class RunCommandTests
{
    [Fact]
    public void A_full_run_with_no_projects_refuses_to_report_success()
    {
        var report = new AnalysisReport
        {
            Mode = "full",
            BaseRef = "origin/main",
            FullRunReasons = ["analysis failed, falling back to a full run: InvalidOperationException: boom"],
            TotalTests = 0,
            SelectedTests = 0,
            Projects = [],
        };

        Assert.True(report.IsFullRun);
        Assert.NotNull(RunCommand.UnrunnableFullRun(report));
    }

    [Fact]
    public void A_full_run_that_named_its_projects_proceeds()
    {
        // The other half of the fix: a failure after the workspace loaded still knows what the
        // test projects are, so the promised full run can actually happen.
        var report = new AnalysisReport
        {
            Mode = "full",
            BaseRef = "origin/main",
            FullRunReasons = ["a full run was requested explicitly"],
            TotalTests = 12,
            SelectedTests = 12,
            Projects = [Project("App.Tests", total: 12, selected: 12)],
        };

        Assert.Null(RunCommand.UnrunnableFullRun(report));
    }

    [Fact]
    public void A_selective_run_that_selected_nothing_is_not_a_refusal()
    {
        // A diff that genuinely impacts no test is the tool working, not failing.
        var report = new AnalysisReport
        {
            Mode = "selective",
            BaseRef = "origin/main",
            TotalTests = 12,
            SelectedTests = 0,
            Projects = [Project("App.Tests", total: 12, selected: 0)],
        };

        Assert.False(report.IsFullRun);
        Assert.Null(RunCommand.UnrunnableFullRun(report));
    }

    [Fact]
    public void A_selective_run_with_no_projects_at_all_is_not_a_refusal()
    {
        // A solution with no test projects is a legitimate, if useless, thing to analyse. The
        // refusal is specifically about a *full* run that cannot name what to run.
        var report = new AnalysisReport
        {
            Mode = "selective",
            BaseRef = "origin/main",
            TotalTests = 0,
            SelectedTests = 0,
            Projects = [],
        };

        Assert.Null(RunCommand.UnrunnableFullRun(report));
    }

    private static ProjectSelection Project(string name, int total, int selected) => new()
    {
        Name = name,
        ProjectPath = $"/repo/tests/{name}/{name}.csproj",
        Framework = "XUnitV3",
        Runner = "MicrosoftTestingPlatform",
        TotalTests = total,
        SelectedTests = selected,
        Filtered = selected != total,
    };
}
