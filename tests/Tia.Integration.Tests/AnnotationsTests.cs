using Tia.Cli;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary>Untested changes as annotations on a pull request's diff (#92).</summary>
public sealed class AnnotationsTests
{
    private static readonly Dictionary<string, string?> OnGitHub = new() { ["GITHUB_ACTIONS"] = "true" };

    private static readonly Dictionary<string, string?> OnAzure = new() { ["TF_BUILD"] = "True" };

    private static AnalysisReport Report(int untested) => new()
    {
        Mode = "selective",
        BaseRef = "origin/main",
        TotalTests = 10,
        SelectedTests = 1,
        UntestedChanges = [.. Enumerable.Range(0, untested).Select(i => new UntestedChange
        {
            Symbol = $"Orders.Refund{i}(Order)",
            Project = "App",
            File = "src/App/Orders.cs",
            Line = 40 + i,
        })],
    };

    [Fact]
    public void An_untested_change_becomes_a_warning_on_its_line()
    {
        var output = Annotations.Render(Report(1), failOnUntested: false);

        Assert.Equal(
            "::warning file=src/App/Orders.cs,line=40,title=No test reaches this::Orders.Refund0(Order) changed, and no test reaches it. " +
            "A change here selects nothing; `dotnet tia explain --symbol` shows the nearest code a test does reach.\n",
            output);
    }

    [Fact]
    public void Under_the_gate_it_is_an_error()
    {
        Assert.StartsWith("::error file=", Annotations.Render(Report(1), failOnUntested: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Ten_are_written_and_one_notice_counts_the_rest()
    {
        var lines = Annotations.Render(Report(25), failOnUntested: false).TrimEnd('\n').Split('\n');

        Assert.Equal(Annotations.MaxPerLevel + 1, lines.Length);
        Assert.All(lines.Take(Annotations.MaxPerLevel), l => Assert.StartsWith("::warning ", l, StringComparison.Ordinal));
        Assert.StartsWith("::notice title=More untested changes::15 more changed symbol(s)", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_untested_writes_nothing()
    {
        Assert.Equal(string.Empty, Annotations.Render(Report(0), failOnUntested: true));
    }

    [Fact]
    public void Values_that_would_end_a_command_are_escaped()
    {
        var report = Report(0) with
        {
            UntestedChanges = [new UntestedChange { Symbol = "A.B(Dictionary<int, string>)\n::error::x", Project = "App", File = "dir,with:colon/A.cs", Line = 3 }],
        };

        var line = Annotations.Render(report, failOnUntested: false);

        Assert.Contains("file=dir%2Cwith%3Acolon/A.cs,line=3,", line, StringComparison.Ordinal);
        Assert.Contains("A.B(Dictionary<int, string>)%0A::error::x", line, StringComparison.Ordinal);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void A_change_without_a_file_is_annotated_without_one()
    {
        var report = Report(0) with { UntestedChanges = [new UntestedChange { Symbol = "A.B()", Project = "App" }] };

        Assert.StartsWith("::warning title=No test reaches this::A.B()", Annotations.Render(report, failOnUntested: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Under_Azure_Pipelines_it_is_a_logged_issue_on_its_line()
    {
        // #128: the same finding, in the logging command Azure Pipelines puts on the build summary
        // and the pull request.
        var output = Annotations.RenderAzure(Report(1), failOnUntested: false);

        Assert.Equal(
            "##vso[task.logissue type=warning;sourcepath=src/App/Orders.cs;linenumber=40;code=tia-untested;]Orders.Refund0(Order) changed, and no test reaches it. " +
            "A change here selects nothing; `dotnet tia explain --symbol` shows the nearest code a test does reach.\n",
            output);
        Assert.StartsWith("##vso[task.logissue type=error;", Annotations.RenderAzure(Report(1), failOnUntested: true), StringComparison.Ordinal);

        var lines = Annotations.RenderAzure(Report(25), failOnUntested: false).TrimEnd('\n').Split('\n');
        Assert.Equal(Annotations.MaxPerLevel + 1, lines.Length);
        Assert.Contains("15 more changed symbol(s)", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Azure_values_that_would_end_a_command_are_escaped()
    {
        var report = Report(0) with
        {
            UntestedChanges = [new UntestedChange { Symbol = "A.B()\n##vso[task.complete result=Failed]100%", Project = "App", File = "dir;x]/A.cs", Line = 3 }],
        };

        var line = Annotations.RenderAzure(report, failOnUntested: false);

        Assert.Contains("sourcepath=dir%3Bx%5D/A.cs;linenumber=3;", line, StringComparison.Ordinal);
        Assert.Contains("A.B()%0A##vso[task.complete result=Failed]100%AZP25", line, StringComparison.Ordinal);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void Each_CI_system_gets_its_own_commands_and_the_summary_is_attached_only_where_it_must_be()
    {
        var github = new StringWriter();
        var azure = new StringWriter();
        Annotations.Write(Report(1), failOnUntested: false, github, OnGitHub);
        Annotations.Write(Report(1), failOnUntested: false, azure, OnAzure);
        Assert.StartsWith("::warning ", github.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("##vso[task.logissue ", azure.ToString(), StringComparison.Ordinal);

        // GitHub reads $GITHUB_STEP_SUMMARY on its own; Azure Pipelines has to be told.
        var attached = new StringWriter();
        Annotations.AttachSummary("summary.md", attached, OnGitHub);
        Assert.Equal(string.Empty, attached.ToString());
        Annotations.AttachSummary("summary.md", attached, OnAzure);
        Assert.Equal($"##vso[task.uploadsummary]{Path.GetFullPath("summary.md")}\n", attached.ToString());
    }

    [Fact]
    public void For_GitLab_it_is_a_code_quality_report()
    {
        var report = Report(2) with
        {
            UntestedChanges = [.. Report(2).UntestedChanges, new UntestedChange { Symbol = "NoFile()", Project = "App" }],
        };

        using var document = System.Text.Json.JsonDocument.Parse(CodeQuality.Render(report, failOnUntested: false));
        var issues = document.RootElement.EnumerateArray().ToList();

        // A change with no file has nowhere to go in a merge request, so it is left out.
        Assert.Equal(2, issues.Count);
        var first = issues[0];
        Assert.Equal("tia-untested", first.GetProperty("check_name").GetString());
        Assert.Equal("minor", first.GetProperty("severity").GetString());
        Assert.Equal("src/App/Orders.cs", first.GetProperty("location").GetProperty("path").GetString());
        Assert.Equal(40, first.GetProperty("location").GetProperty("lines").GetProperty("begin").GetInt32());
        Assert.StartsWith("Orders.Refund0(Order) changed", first.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(first.GetProperty("fingerprint").GetString(), issues[1].GetProperty("fingerprint").GetString());

        // Stable, so GitLab can tell a finding it has seen from a new one; and major under the gate.
        Assert.Equal(CodeQuality.Render(report, failOnUntested: false), CodeQuality.Render(report, failOnUntested: false));
        Assert.Contains("\"severity\": \"major\"", CodeQuality.Render(report, failOnUntested: true), StringComparison.Ordinal);
        Assert.Equal("[]", CodeQuality.Render(Report(0), failOnUntested: false).Trim());
    }

    [Fact]
    public void Outside_GitHub_Actions_nothing_is_written()
    {
        var output = new StringWriter();

        Annotations.Write(Report(3), failOnUntested: false, output, new Dictionary<string, string?>());
        Assert.Equal(string.Empty, output.ToString());

        Annotations.Write(Report(3), failOnUntested: false, output, OnGitHub);
        Assert.Equal(3, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
