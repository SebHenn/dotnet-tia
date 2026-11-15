using Tia.Cli.Commands;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary><c>run --budget</c> (#174): projects not started are left unstarted, never skipped silently.</summary>
public sealed class BudgetTests
{
    private static ProjectSelection Project(string name) => new()
    {
        Name = name,
        ProjectPath = $"/repo/{name}.csproj",
        Framework = "XUnitV3",
        Runner = "VsTest",
        TotalTests = 1,
        SelectedTests = 1,
        Filtered = false,
    };

    private static readonly IReadOnlyList<ProjectSelection> Three = [Project("A"), Project("B"), Project("C")];

    [Fact]
    public void An_exhausted_budget_still_runs_the_first_project_and_starts_no_other()
    {
        var started = new List<string>();

        var results = SelectiveRun.RunInTurn(Three, (project, _, _, _) =>
        {
            started.Add(project.Name);
            return new SelectiveRun.ProjectRun(0, Ran: true, Abandoned: false);
        }, CancellationToken.None, mayStart: () => false);

        Assert.Equal(["A"], started);
        Assert.Single(results);
    }

    [Fact]
    public void A_budget_with_time_left_runs_everything()
    {
        var results = SelectiveRun.RunInTurn(
            Three,
            (_, _, _, _) => new SelectiveRun.ProjectRun(0, Ran: true, Abandoned: false),
            CancellationToken.None,
            mayStart: () => true);

        Assert.Equal(3, results.Count);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData("5M", 300)]
    public void A_duration_is_a_whole_number_and_a_unit(string token, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RunCommand.ParseBudget(token));

    [Theory]
    [InlineData("5")]
    [InlineData("m")]
    [InlineData("0m")]
    [InlineData("-5m")]
    [InlineData("1.5m")]
    [InlineData("5d")]
    [InlineData("")]
    public void Anything_else_is_not_a_duration(string token) =>
        Assert.Null(RunCommand.ParseBudget(token));
}
