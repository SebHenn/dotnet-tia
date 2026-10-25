using System.Reflection;
using System.Text.RegularExpressions;
using Tia.Cli;
using Tia.Cli.Commands;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary>The job-page summary (#71): what a reviewer reads without running anything.</summary>
public sealed class MarkdownSummaryTests
{
    private static AnalysisReport Report(string mode = "selective", IReadOnlyList<string>? reasons = null) => new()
    {
        Mode = mode,
        BaseRef = "origin/main",
        BaseCommit = "73be09d0abcdef",
        FullRunReasons = reasons ?? [],
        TotalTests = 200,
        ImpactedTests = 10,
        SelectedTests = 12,
        ElapsedSeconds = 4.0,
        Diff = new DiffSummary { FileCount = 2, CSharpFileCount = 2, ChangedSymbolCount = 3 },
        Projects =
        [
            new ProjectSelection
            {
                Name = "App.Tests", ProjectPath = "/repo/App.Tests.csproj", Framework = "XUnitV3", Runner = "VsTest",
                TotalTests = 200, SelectedTests = 12, Filtered = true,
            },
        ],
    };

    [Fact]
    public void A_selective_run_leads_with_how_much_was_selected()
    {
        var summary = MarkdownSummary.Render(Report());

        Assert.StartsWith("### dotnet-tia: 12 of 200 tests selected (6.0 %)", summary, StringComparison.Ordinal);
        Assert.Contains("| App.Tests | 12 | 200 | filtered |", summary, StringComparison.Ordinal);
        Assert.Contains("Selecting pays off when the full suite takes more than 4 s.", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_run_says_why()
    {
        var summary = MarkdownSummary.Render(Report("full", ["global.json changed"]));

        Assert.StartsWith("### dotnet-tia: full run, 200 tests", summary, StringComparison.Ordinal);
        Assert.Contains("- global.json changed", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Untested_changes_are_listed()
    {
        var report = Report() with
        {
            UntestedChanges = [new UntestedChange { Symbol = "Orders.Refund(List<Order>)", Project = "App", File = "src/App/Orders.cs" }],
        };

        var summary = MarkdownSummary.Render(report);

        Assert.Contains("#### Changed code no test reaches", summary, StringComparison.Ordinal);
        Assert.Contains("- `Orders.Refund(List<Order>)` in `src/App/Orders.cs`", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_widenings_are_folded_after_the_first_few()
    {
        var report = Report() with
        {
            Widenings = [.. Enumerable.Range(1, 8).Select(i => new WideningEvent("Reflection", $"P{i}", "uses reflection"))],
        };

        var summary = MarkdownSummary.Render(report);

        Assert.Contains("- **Reflection** `P5`: uses reflection", summary, StringComparison.Ordinal);
        Assert.Contains("<details><summary>3 more</summary>", summary, StringComparison.Ordinal);
        Assert.True(summary.IndexOf("`P6`", StringComparison.Ordinal) > summary.IndexOf("<details>", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_from_the_repository_cannot_become_markup()
    {
        // A diagnostic or a project name is the repository's text, not ours: a pipe would end a
        // table cell, and an angle bracket would open HTML on the job page.
        var report = Report("full", ["<img src=x> | *bold*"]) with
        {
            Projects = [Report().Projects[0] with { Name = "A|B<script>" }],
        };

        var summary = MarkdownSummary.Render(report);

        Assert.Contains(@"- \<img src=x\> \| \*bold\*", summary, StringComparison.Ordinal);
        Assert.Contains(@"| A\|B\<script\> |", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Plain", "`Plain`")]
    [InlineData("List`1", "``List`1``")]
    [InlineData("`edge", "`` `edge ``")]
    public void A_code_span_survives_backticks_inside_it(string text, string expected)
    {
        Assert.Equal(expected, MarkdownSummary.Code(text));
    }

    [Fact]
    public void A_run_states_its_outcome()
    {
        var summary = MarkdownSummary.Render(Report(), RunCommand.Outcome(Report(), dryRun: false, exitCode: 1));

        Assert.Contains("**The tests failed (exit 1).**", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0, 12, "The selected tests passed.")]
    [InlineData(false, 0, 0, "Nothing was selected, so no tests ran.")]
    [InlineData(false, 2, 12, "Nothing could be concluded (exit 2).")]
    [InlineData(true, 0, 12, "Dry run: nothing was run.")]
    public void The_outcome_line_follows_the_exit_code(bool dryRun, int exitCode, int selected, string expected)
    {
        var report = Report() with { SelectedTests = selected };

        Assert.Equal(expected, RunCommand.Outcome(report, dryRun, exitCode));
    }

    [Fact]
    public void The_summary_is_appended_not_overwritten()
    {
        // $GITHUB_STEP_SUMMARY is shared by everything in the step.
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "earlier\n");

            MarkdownSummary.AppendTo(path, Report());

            var text = File.ReadAllText(path);
            Assert.StartsWith("earlier\n### dotnet-tia:", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_huge_report_stays_far_below_the_limit_GitHub_drops_a_summary_at()
    {
        // #100: GitHub drops a step summary over 1 MiB, and a large refactor can produce thousands
        // of untested changes. The summary must survive exactly then.
        var report = Report() with
        {
            UntestedChanges = [.. Enumerable.Range(0, 10_000).Select(i => new UntestedChange
            {
                Symbol = $"Orders.Generated.Method{i}(List<Order>, CancellationToken)", Project = "App", File = $"src/App/Generated/File{i}.cs",
            })],
            Widenings = [.. Enumerable.Range(0, 5_000).Select(i => new WideningEvent("Reflection", $"App{i}", new string('x', 2_000)))],
        };

        var summary = MarkdownSummary.Render(report);

        Assert.True(summary.Length < 64 * 1024, $"{summary.Length} characters");
        Assert.Contains("...and 9,950 more untested change(s); the full list is in the JSON report", summary, StringComparison.Ordinal);
        Assert.Contains("...and 4,900 more widening(s)", summary, StringComparison.Ordinal);
        Assert.Contains("Orders.Generated.Method49(", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Orders.Generated.Method50(", summary, StringComparison.Ordinal);

        // The details block still closes, so the rest of the page renders.
        Assert.Equal(Regex.Count(summary, "<details>"), Regex.Count(summary, "</details>"));
    }

    [Fact]
    public void Load_failures_are_capped_too()
    {
        var report = Report() with { LoadFailures = [.. Enumerable.Range(0, 1_000).Select(i => $"Project{i}.csproj did not load")] };

        var summary = MarkdownSummary.Render(report);

        Assert.Contains("- Project24.csproj did not load", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Project25.csproj", summary, StringComparison.Ordinal);
        Assert.Contains("...and 975 more; the full list is in the JSON report", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_list_is_shown_whole_with_no_note()
    {
        var report = Report() with
        {
            UntestedChanges = [.. Enumerable.Range(0, MarkdownSummary.MaxUntestedChanges).Select(i => new UntestedChange { Symbol = $"M{i}()", Project = "App" })],
        };

        Assert.DoesNotContain("...and", MarkdownSummary.Render(report), StringComparison.Ordinal);
    }
}

/// <summary>The composite action at the repository root (#71).</summary>
public sealed class ActionDefinitionTests
{
    private static string RepositoryFile(string name)
    {
        var directory = typeof(ActionDefinitionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "RepositoryDirectory").Value!;

        return File.ReadAllText(Path.Combine(directory, name));
    }

    [Fact]
    public void The_action_installs_the_release_it_belongs_to()
    {
        // A tag's action installs the tag's tool only if the stamp moved with the version. The
        // release tags cannot be moved, so a mismatch shipped is a mismatch kept.
        var stamp = Regex.Match(RepositoryFile("action.yml"), @"TIA_ACTION_VERSION: '([^']+)'").Groups[1].Value;
        var version = Regex.Match(RepositoryFile("Directory.Build.props"), @"<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value;

        Assert.NotEmpty(version);
        Assert.Equal(version, stamp);
    }
}

/// <summary><c>--report &lt;file&gt;</c> (#75): how <c>run</c>'s selection reaches a later job.</summary>
public sealed class ReportFileTests
{
    [Fact]
    public void The_report_is_written_as_json_to_the_file()
    {
        var common = new CommonOptions();
        var command = new System.CommandLine.Command("analyze");
        command.Options.Add(common.ReportFile);

        var path = Path.GetTempFileName();
        try
        {
            var report = new AnalysisReport
            {
                Mode = "selective",
                BaseRef = "origin/main",
                TotalTests = 3,
                SelectedTests = 1,
                SkippedTests = [new SkippedTests { Project = "App.Tests", Tests = ["App.Tests.B", "App.Tests.C"] }],
            };

            common.WriteReport(command.Parse(["--report", path]), report);

            var json = File.ReadAllText(path);
            Assert.StartsWith("{", json, StringComparison.Ordinal);
            Assert.Contains("\"skippedTests\"", json, StringComparison.Ordinal);
            Assert.Contains("App.Tests.C", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
