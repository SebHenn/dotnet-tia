using Tia.Core.Reporting;

namespace Tia.Core.Tests;

/// <summary>
/// Whether selection is actually paying off, from runs that happened.
/// </summary>
/// <remarks>
/// The tool measured <c>A</c> and <c>f</c> on every run and never timed the suite it had just
/// spawned, so it could print "worth it if the full suite takes more than 14s" having just watched
/// that suite take two. These pin the arithmetic, and pin that the unfavourable answer is the one it
/// is willing to give.
/// </remarks>
public sealed class RunLedgerTests
{
    private static RunRecord Run(bool full, double analysis, double suite, int selected, int total = 221) =>
        new("2026-08-15T18:00:00Z", "abc", full, analysis, suite, selected, total);

    /// <summary>
    /// Cartographer's measured figures: a 6.85 s analysis against a suite its own design document
    /// times at about two seconds. The whole reason this file exists.
    /// </summary>
    [Fact]
    public void A_fast_suite_is_reported_as_a_loss()
    {
        var verdict = RunLedger.Assess([
            Run(full: true, analysis: 6.9, suite: 2.0, selected: 221),
            Run(full: false, analysis: 6.8, suite: 1.9, selected: 210),
            Run(full: false, analysis: 6.9, suite: 1.2, selected: 140),
        ]);

        Assert.NotNull(verdict);
        Assert.True(verdict.SuiteObserved);
        Assert.Equal(2.0, verdict.SuiteSeconds, 3);
        Assert.True(verdict.NetSecondsPerRun < 0, "a 6.8s analysis cannot pay for a 2s suite");

        var advice = RunLedger.Advice(verdict);
        Assert.NotNull(advice);
        Assert.Contains("costing you time", advice, StringComparison.Ordinal);
    }

    /// <summary>The other direction, so the message is not simply always pessimistic.</summary>
    [Fact]
    public void A_slow_suite_with_narrow_selection_is_reported_as_a_saving()
    {
        var verdict = RunLedger.Assess([
            Run(full: true, analysis: 7.0, suite: 300, selected: 2000, total: 2000),
            Run(full: false, analysis: 7.0, suite: 30, selected: 200, total: 2000),
            Run(full: false, analysis: 7.0, suite: 30, selected: 200, total: 2000),
        ]);

        Assert.NotNull(verdict);
        Assert.True(verdict.NetSecondsPerRun > 0);
        Assert.Null(RunLedger.Advice(verdict));
    }

    /// <summary>
    /// Three runs is the floor, and it must include a selective one: `A + fT` is not observable from
    /// full runs alone, and a verdict from one sample would be noise wearing a recommendation.
    /// </summary>
    [Fact]
    public void Too_little_evidence_yields_no_verdict()
    {
        Assert.Null(RunLedger.Assess([]));
        Assert.Null(RunLedger.Assess([Run(false, 7, 1, 10), Run(false, 7, 1, 10)]));
        Assert.Null(RunLedger.Assess([Run(true, 7, 1, 221), Run(true, 7, 1, 221), Run(true, 7, 1, 221)]));
    }

    /// <summary>
    /// A run whose suite never executed says nothing about `T`, and averaging a zero into it would
    /// invent a saving. Dry runs and "nothing to run" outcomes are exactly that shape.
    /// </summary>
    [Fact]
    public void A_run_with_no_suite_time_is_not_evidence() =>
        Assert.Null(RunLedger.Assess([Run(false, 7, 0, 10), Run(false, 7, 0, 10), Run(false, 7, 0, 10)]));

    /// <summary>
    /// Without a full run, `T` is a lower bound - the most any selective run was seen to execute.
    /// That understates the saving rather than inventing one, and the message says so.
    /// </summary>
    [Fact]
    public void Without_a_full_run_the_suite_time_is_a_lower_bound()
    {
        var verdict = RunLedger.Assess([
            Run(full: false, analysis: 7.0, suite: 1.0, selected: 50),
            Run(full: false, analysis: 7.0, suite: 2.0, selected: 100),
            Run(full: false, analysis: 7.0, suite: 1.5, selected: 75),
        ]);

        Assert.NotNull(verdict);
        Assert.False(verdict.SuiteObserved);
        Assert.Equal(2.0, verdict.SuiteSeconds, 3);
        Assert.Contains("understates the loss", RunLedger.Advice(verdict)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// This machine is de-DE, so a plain interpolation writes "6,8s" while the advice sentence,
    /// which formats invariantly, writes "6.8s" - two spellings of one number in adjacent lines.
    /// </summary>
    [Fact]
    public void The_figures_are_formatted_invariantly()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

        try
        {
            var formatted = RunLedger.Format(new LedgerVerdict(3, 6.8, 0.62, 2.0, SuiteObserved: true));

            Assert.Contains("6.8s", formatted, StringComparison.Ordinal);
            Assert.DoesNotContain("6,8s", formatted, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    /// <summary>A round trip through the file, including the trimming that keeps it bounded.</summary>
    [Fact]
    public void The_ledger_round_trips_and_stays_bounded()
    {
        var directory = Directory.CreateTempSubdirectory("tia-ledger-");

        try
        {
            for (var i = 0; i < RunLedger.Capacity + 5; i++)
            {
                RunLedger.Append(directory.FullName, Run(false, 7, 1, i));
            }

            var records = RunLedger.Read(directory.FullName);

            Assert.Equal(RunLedger.Capacity, records.Count);

            // The newest survive, not the oldest: the question is whether it pays off *lately*.
            Assert.Equal(RunLedger.Capacity + 4, records[^1].SelectedTests);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A killed run can leave half a line. That must cost the line, not the file - and must never
    /// throw, because this is advice and the run it describes already succeeded.
    /// </summary>
    [Fact]
    public void A_corrupt_line_costs_only_itself()
    {
        var directory = Directory.CreateTempSubdirectory("tia-ledger-");

        try
        {
            RunLedger.Append(directory.FullName, Run(false, 7, 1, 10));
            File.AppendAllText(RunLedger.PathIn(directory.FullName), "{\"at\":\"trunc" + Environment.NewLine);
            RunLedger.Append(directory.FullName, Run(false, 7, 1, 20));

            var records = RunLedger.Read(directory.FullName);

            Assert.Equal(2, records.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
