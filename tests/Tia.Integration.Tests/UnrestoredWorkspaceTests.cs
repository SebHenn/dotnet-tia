using Tia.Cli;
using Tia.Cli.Commands;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// The rule: a test project that resolved no tests is a failed load, not a repository with no
/// tests in it.
/// </summary>
/// <remarks>
/// <para>
/// Found on cartographer (issue #20). A fresh CI runner has no <c>obj/</c>, so
/// <c>MSBuildWorkspace</c> cannot resolve a <c>PackageReference</c> - it hands back a compilation
/// in which xunit's <c>[Fact]</c> is an error type. The solution opens, project references resolve,
/// 142 of 143 types are built, and no method in the suite is a test. <c>graph</c> printed
/// "0 tests discovered across 1 test project(s)" and exited 0; <c>analyze</c> selected a full run
/// of nothing and printed its break-even arithmetic; <c>shadow</c> reported "nothing at risk" while
/// watching an empty set. Only <c>verify</c> caught it, and only because it refuses to pass without
/// a usable sample.
/// </para>
/// <para>
/// The unit tests below pin the rule; <see cref="UnrestoredSolutionTests"/> loads a genuinely
/// unrestored solution and pins the whole path through it.
/// </para>
/// </remarks>
public sealed class WorkspaceHealthTests
{
    private const string Suite = "/repo/test/Suite/Suite.csproj";

    private static ProjectDescriptor Project(
        string filePath,
        bool isTestProject = true,
        TestFramework framework = TestFramework.XUnitV3,
        string? name = null) => new()
    {
        Name = name ?? Path.GetFileNameWithoutExtension(filePath),
        AssemblyName = Path.GetFileNameWithoutExtension(filePath),
        FilePath = filePath,
        ProjectReferences = [],
        IsTestProject = isTestProject,
        Framework = framework,
    };

    private static TestMethod Test(string projectName) => new()
    {
        SymbolKey = "M:SomeTests.Works",
        ClassKey = "T:SomeTests",
        Namespace = "Suite",
        ClassName = "SomeTests",
        MethodName = "Works",
        ProjectName = projectName,
        Framework = TestFramework.XUnitV3,
    };

    /// <summary>The issue itself: no assets file, so the message is the one command that fixes it.</summary>
    [Fact]
    public void An_unrestored_test_project_names_the_missing_restore()
    {
        var failure = Assert.Single(WorkspaceHealth.Diagnose(
            [Project(Suite, framework: TestFramework.Unknown)], [], _ => false));

        Assert.Contains("Suite", failure, StringComparison.Ordinal);
        Assert.Contains("0 tests", failure, StringComparison.Ordinal);
        Assert.Contains("project.assets.json", failure, StringComparison.Ordinal);
        Assert.Contains("dotnet restore", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restored project that still references no test framework has some other resolution
    /// problem, and telling it to restore again is advice that has already been taken.
    /// </summary>
    [Fact]
    public void A_restored_project_with_no_framework_is_not_told_to_restore()
    {
        var failure = Assert.Single(WorkspaceHealth.Diagnose(
            [Project(Suite, framework: TestFramework.Unknown)], [], _ => true));

        Assert.Contains("references no test framework", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet restore", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// Restored, framework resolved, and still empty. Nothing can ever be selected from it, so it
    /// is still reported - and the way out is the property the tool already honours to leave a
    /// project out of the suite, rather than a flag invented for this.
    /// </summary>
    [Fact]
    public void A_resolved_test_project_holding_no_tests_is_still_reported()
    {
        var failure = Assert.Single(WorkspaceHealth.Diagnose([Project(Suite)], [], _ => true));

        Assert.Contains("nothing in it can ever be selected", failure, StringComparison.Ordinal);
        Assert.Contains("IsTestProject", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_project_with_tests_is_not_a_failure()
    {
        Assert.Empty(WorkspaceHealth.Diagnose([Project(Suite)], [Test("Suite")], _ => true));
    }

    /// <summary>Nothing is concluded about a project that was never going to hold tests.</summary>
    [Fact]
    public void A_production_project_holding_no_tests_is_not_a_failure()
    {
        Assert.Empty(WorkspaceHealth.Diagnose(
            [Project("/repo/src/Lib/Lib.csproj", isTestProject: false, framework: TestFramework.Unknown)],
            [],
            _ => false));
    }

    /// <summary>
    /// A multi-targeted test project arrives as one loaded project per framework, and conditional
    /// compilation can legitimately leave one of them with no tests. The suite is the file, so the
    /// whole group has to be empty before anything is wrong.
    /// </summary>
    [Fact]
    public void One_framework_of_a_multi_targeted_suite_holding_no_tests_is_not_a_failure()
    {
        Assert.Empty(WorkspaceHealth.Diagnose(
            [Project(Suite, name: "Suite(net10.0)"), Project(Suite, name: "Suite(net462)")],
            [Test("Suite(net10.0)")],
            _ => true));
    }

    /// <summary>And when the whole file is empty it is one failure naming the file, not two.</summary>
    [Fact]
    public void A_multi_targeted_suite_holding_no_tests_is_reported_once()
    {
        var failure = Assert.Single(WorkspaceHealth.Diagnose(
            [Project(Suite, name: "Suite(net10.0)"), Project(Suite, name: "Suite(net462)")],
            [],
            _ => true));

        Assert.StartsWith("Suite is a test project", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal the verdict drives. The existing guard cannot catch this state - the test
    /// projects are all present and enumerable, they simply hold nothing - so `run` would have
    /// invoked a suite the analysis never saw and reported whatever came back.
    /// </summary>
    [Fact]
    public void An_unusable_report_stops_run_before_it_invokes_anything()
    {
        var report = new AnalysisReport
        {
            Mode = "full",
            BaseRef = "HEAD",
            TotalTests = 0,
            SelectedTests = 0,
            LoadFailures = ["Suite is a test project and yielded 0 tests"],
            Projects =
            [
                new ProjectSelection
                {
                    Name = "Suite",
                    ProjectPath = Suite,
                    Framework = nameof(TestFramework.Unknown),
                    Runner = nameof(TestRunner.Unknown),
                    TotalTests = 0,
                    SelectedTests = 0,
                    Filtered = false,
                },
            ],
        };

        Assert.True(report.WorkspaceUnusable);

        var refusal = RunCommand.UnrunnableFullRun(report);
        Assert.NotNull(refusal);
        Assert.Contains("did not resolve", refusal, StringComparison.Ordinal);
    }

    /// <summary>An ordinary full run is still a full run, and still runs.</summary>
    [Fact]
    public void A_healthy_full_run_report_is_still_runnable()
    {
        var report = new AnalysisReport
        {
            Mode = "full",
            BaseRef = "HEAD",
            TotalTests = 12,
            SelectedTests = 12,
            Projects =
            [
                new ProjectSelection
                {
                    Name = "Suite",
                    ProjectPath = Suite,
                    Framework = nameof(TestFramework.XUnitV3),
                    Runner = nameof(TestRunner.VsTest),
                    TotalTests = 12,
                    SelectedTests = 12,
                    Filtered = false,
                },
            ],
        };

        Assert.False(report.WorkspaceUnusable);
        Assert.Null(RunCommand.UnrunnableFullRun(report));
    }

    /// <summary>
    /// The two blocks are different statements and must not collapse into one list: a full run is
    /// a decision, a failed load is the absence of one.
    /// </summary>
    [Fact]
    public void The_render_separates_a_failed_load_from_ordinary_conservatism()
    {
        var rendered = ReportRenderer.Render(
            new AnalysisReport
            {
                Mode = "full",
                BaseRef = "HEAD",
                TotalTests = 0,
                SelectedTests = 0,
                FullRunReasons = ["Suite yielded nothing", "a full run was requested explicitly"],
                LoadFailures = ["Suite yielded nothing"],
            },
            verbose: false);

        Assert.Contains("WORKSPACE DID NOT RESOLVE", rendered, StringComparison.Ordinal);
        Assert.Contains("x Suite yielded nothing", rendered, StringComparison.Ordinal);

        // Printed once, under the heading that reads it correctly.
        Assert.DoesNotContain("! Suite yielded nothing", rendered, StringComparison.Ordinal);
        Assert.Contains("! a full run was requested explicitly", rendered, StringComparison.Ordinal);

        // The break-even is arithmetic over a suite that was never seen.
        Assert.Contains("unanswerable", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_full_run_renders_exactly_as_it_did()
    {
        var rendered = ReportRenderer.Render(
            new AnalysisReport
            {
                Mode = "full",
                BaseRef = "HEAD",
                TotalTests = 12,
                SelectedTests = 12,
                FullRunReasons = ["a full run was requested explicitly"],
            },
            verbose: false);

        Assert.DoesNotContain("WORKSPACE DID NOT RESOLVE", rendered, StringComparison.Ordinal);
        Assert.Contains("FULL RUN - selection was not applied", rendered, StringComparison.Ordinal);
        Assert.Contains("! a full run was requested explicitly", rendered, StringComparison.Ordinal);
    }
}

/// <summary>
/// The same rule, end to end, against a solution that has genuinely never been restored - the
/// state every CI runner starts in.
/// </summary>
[Collection(nameof(UnrestoredFixtureCollection))]
public sealed class UnrestoredSolutionTests(UnrestoredFixtureRepository repository)
{
    [Fact]
    public async Task An_unrestored_solution_reports_a_failed_load_rather_than_an_empty_suite()
    {
        var report = await repository.AnalyzeAsync();

        // The shape of the bug: the graph is built, the test projects are listed, and there is not
        // a test in any of them.
        Assert.True(report.Graph.Types > 0);
        Assert.NotEmpty(report.Projects);
        Assert.Equal(0, report.TotalTests);

        // ... and that is now said, rather than reported as a number with no baseline.
        Assert.True(report.WorkspaceUnusable);
        Assert.All(report.LoadFailures, f => Assert.Contains("dotnet restore", f, StringComparison.Ordinal));
        Assert.Contains(report.LoadFailures, f => f.StartsWith("Fixtures.Tests", StringComparison.Ordinal));

        // Every load failure is a full-run reason too, so nothing reading the older field breaks.
        Assert.All(report.LoadFailures, f => Assert.Contains(f, report.FullRunReasons));

        // The command that would otherwise invoke a suite the analysis never saw.
        Assert.NotNull(RunCommand.UnrunnableFullRun(report));
    }
}

[CollectionDefinition(nameof(UnrestoredFixtureCollection))]
public sealed class UnrestoredFixtureCollection : ICollectionFixture<UnrestoredFixtureRepository>;
