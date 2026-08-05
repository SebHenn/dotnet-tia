using Tia.Cli.Commands;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary>
/// The verdict `explain` reaches about a single test.
/// </summary>
/// <remarks>
/// `explain` exists because the first question any adopter asks is "why did it pick this test", or
/// worse, "why didn't it" - a tool that cannot answer either does not get trusted with the decision
/// to skip tests. So a confidently wrong answer here is worse than no answer, and it gave one: any
/// widening on the project was read as "the project runs whole", which made it report "selected"
/// for a test genuinely absent from the selection on NodaTime.
/// </remarks>
public sealed class ExplainCommandTests
{
    [Fact]
    public void A_test_with_a_path_is_reached()
    {
        Assert.Equal(
            ExplainCommand.Verdict.Reached,
            ExplainCommand.VerdictFor(reachedByPath: true, Filtered("App.Tests")));
    }

    [Fact]
    public void A_path_wins_even_when_the_project_runs_unfiltered()
    {
        // Both are true; the path is the more informative answer and the one the user asked for.
        Assert.Equal(
            ExplainCommand.Verdict.Reached,
            ExplainCommand.VerdictFor(reachedByPath: true, Unfiltered("App.Tests")));
    }

    [Fact]
    public void An_unreached_test_in_an_unfiltered_project_still_runs()
    {
        Assert.Equal(
            ExplainCommand.Verdict.ProjectRunsUnfiltered,
            ExplainCommand.VerdictFor(reachedByPath: false, Unfiltered("App.Tests")));
    }

    [Fact]
    public void An_unreached_test_whose_project_is_absent_says_so()
    {
        Assert.Equal(
            ExplainCommand.Verdict.ProjectAbsent,
            ExplainCommand.VerdictFor(reachedByPath: false, selection: null));
    }

    [Fact]
    public void An_unreached_test_in_a_filtered_project_is_not_selected()
    {
        // The branch that used to be reported as "selected" whenever the project carried any
        // widening at all.
        Assert.Equal(
            ExplainCommand.Verdict.NotReached,
            ExplainCommand.VerdictFor(reachedByPath: false, Filtered("App.Tests")));
    }

    private static ProjectSelection Filtered(string name) => Selection(name, filtered: true);

    private static ProjectSelection Unfiltered(string name) => Selection(name, filtered: false);

    private static ProjectSelection Selection(string name, bool filtered) => new()
    {
        Name = name,
        ProjectPath = $"/repo/tests/{name}/{name}.csproj",
        Framework = "XUnitV3",
        Runner = "MicrosoftTestingPlatform",
        TotalTests = 10,
        SelectedTests = filtered ? 1 : 10,
        Filtered = filtered,
        UnfilteredReason = filtered ? null : "selection covers 100% of the project",
    };
}
