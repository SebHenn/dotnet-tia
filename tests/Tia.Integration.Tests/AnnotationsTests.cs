using Tia.Cli;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary>Untested changes as annotations on a pull request's diff (#92).</summary>
public sealed class AnnotationsTests
{
    private static readonly Dictionary<string, string?> OnGitHub = new() { ["GITHUB_ACTIONS"] = "true" };

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
    public void Outside_GitHub_Actions_nothing_is_written()
    {
        var output = new StringWriter();

        Annotations.Write(Report(3), failOnUntested: false, output, new Dictionary<string, string?>());
        Assert.Equal(string.Empty, output.ToString());

        Annotations.Write(Report(3), failOnUntested: false, output, OnGitHub);
        Assert.Equal(3, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
