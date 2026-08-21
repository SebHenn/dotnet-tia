using System.CommandLine;
using System.Globalization;
using Tia.Cli;
using Tia.Cli.Commands;

namespace Tia.Integration.Tests;

/// <summary>
/// What the command line accepts, and what it refuses.
/// </summary>
/// <remarks>
/// Every command used to be given every shared option, and three of them read only some. An
/// option that parses, prints in <c>--help</c> and is then discarded is worse than a missing one:
/// <c>verify --base v1.0</c> was accepted and the harness mutated the working tree regardless, so
/// the run answered a different question than the one asked and said nothing about it. These
/// assert the scoping in both directions, because scoping an option off the wrong command is the
/// same defect with the sign flipped.
/// </remarks>
public sealed class CommandSurfaceTests
{
    [Theory]
    [InlineData("analyze")]
    [InlineData("run")]
    [InlineData("explain")]
    [InlineData("watch")]
    public void A_command_that_resolves_a_diff_takes_the_options_that_describe_one(string name)
    {
        var command = Find(name);

        Assert.Contains(command.Options, o => o.Name == "--base");
        Assert.Contains(command.Options, o => o.Name == "--full");
        Assert.Contains(command.Options, o => o.Name == "--default-branch");
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("verify")]
    [InlineData("replay")]
    public void A_command_that_resolves_no_diff_refuses_the_options_that_describe_one(string name)
    {
        // `graph` forces a full run itself, `verify` writes its own mutation, and `replay` measures
        // each commit against its own parent - so a base revision has nothing to select against and
        // `--full` would defeat the check outright.
        var command = Find(name);

        Assert.DoesNotContain(command.Options, o => o.Name == "--base");
        Assert.DoesNotContain(command.Options, o => o.Name == "--full");
        Assert.DoesNotContain(command.Options, o => o.Name == "--default-branch");
    }

    /// <summary>
    /// The one option `replay` must not accept. It checks out historical revisions, so a solution
    /// path given once is pinned to today's layout: a solution moved inside the walked range then
    /// resolves against a tree that does not contain it, and every commit before the move is
    /// skipped. That silently turned a replay of 20 commits into a report on 1.
    /// </summary>
    [Fact]
    public void Replay_refuses_a_solution_path()
    {
        Assert.DoesNotContain(Find("replay").Options, o => o.Name == "--solution");
        Assert.NotEmpty(Parse("replay --solution tia.slnx").Errors);
    }

    [Fact]
    public void An_option_a_command_ignores_is_a_usage_error_rather_than_a_no_op()
    {
        Assert.NotEmpty(Parse("verify --base origin/main").Errors);
        Assert.NotEmpty(Parse("graph --full").Errors);
    }

    [Theory]
    [InlineData("analyze")]
    [InlineData("explain")]
    [InlineData("graph")]
    [InlineData("verify")]
    [InlineData("replay")]
    public void Json_is_offered_only_where_it_is_implemented(string name)
    {
        Assert.Contains(Find(name).Options, o => o.Name == "--json");
    }

    [Fact]
    public void Watch_does_not_offer_json()
    {
        // Same reason as `run`, one step further: watch emits a report per edit, so there is not
        // even a single analysis for a JSON document to describe.
        Assert.DoesNotContain(Find("watch").Options, o => o.Name == "--json");
    }

    [Fact]
    public void Run_does_not_offer_json()
    {
        // It interleaves its own output with the test runner's, so no JSON document could own the
        // stream. Offering the flag and printing a report in the middle of test output would be a
        // worse answer than not offering it.
        Assert.DoesNotContain(Find("run").Options, o => o.Name == "--json");
    }

    [Theory]
    [InlineData("--cache-dir /tmp/cache")]
    [InlineData("--max-filter-length 8000")]
    [InlineData("--coverage-threshold 0.9")]
    public void The_knobs_that_decide_how_much_runs_are_settable(string argument)
    {
        // CacheDirectory, MaxFilterLength and CoverageThreshold were read by the analyser and
        // assigned by nobody, so the two that decide when a project abandons its filter and runs
        // whole - the numbers an adopter is most likely to contest - could not be changed at all.
        Assert.Empty(Parse("analyze " + argument).Errors);
    }

    [Theory]
    [InlineData("--coverage-threshold 1.5")]
    [InlineData("--coverage-threshold -0.1")]
    [InlineData("--max-filter-length 0")]
    public void A_knob_set_out_of_range_is_rejected_rather_than_obeyed(string argument)
    {
        // Out of range does not fail loudly on its own - it quietly changes how much of the suite
        // executes, which is the shape of every other defect this tool has had.
        Assert.NotEmpty(Parse("analyze " + argument).Errors);
    }

    [Theory]
    [InlineData("--coverage-threshold abc")]
    [InlineData("--max-filter-length abc")]
    public void A_knob_set_to_something_that_is_not_a_number_is_a_parse_error_not_a_crash(string argument)
    {
        // The range checks lived in validators that read the value through GetValueOrDefault, which
        // rethrows a failed conversion - so a non-numeric value did not become a parse error, it
        // escaped as an unhandled InvalidOperationException and printed a stack trace at whoever
        // typed it. Parsing has to fail the way every other bad argument fails.
        var errors = Parse("analyze " + argument).Errors;

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Message.Contains("is not a number", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    [InlineData("")]
    public void A_fraction_is_read_the_same_way_in_every_culture(string culture)
    {
        // `--coverage-threshold 0.6` was parsed with the *current* culture, so on any machine whose
        // decimal separator is a comma it became 6 and the range validator rejected it as "not a
        // fraction between 0 and 1" - which is exactly what had been typed. The documented
        // invocation did not work on most of Europe and South America, and the error blamed the
        // input. This ran green everywhere until someone ran it on a comma-decimal machine, so the
        // culture is pinned here rather than inherited.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            var accepted = Parse("analyze --coverage-threshold 0.6");
            Assert.Empty(accepted.Errors);

            // And the out-of-range guard still has to bite, rather than every value now parsing.
            Assert.NotEmpty(Parse("analyze --coverage-threshold 1.5").Errors);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Asserted against the surface <c>Main</c> actually builds, not against a list this test writes
    /// for itself. It used to construct its own root from five commands and assert those five, so it
    /// agreed with itself by construction - which is how <c>shadow</c> shipped without ever
    /// appearing here.
    /// </summary>
    [Fact]
    public void Every_command_is_reachable_by_name()
    {
        Assert.Equal(
            ["analyze", "explain", "graph", "replay", "run", "shadow", "stats", "verify", "watch"],
            Root().Subcommands.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    private static RootCommand Root() => Program.BuildRoot(new CommonOptions());

    private static Command Find(string name) => Root().Subcommands.Single(c => c.Name == name);

    private static ParseResult Parse(string commandLine) =>
        Root().Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
