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
    private static RunRecord Run(bool full, double analysis, double suite, int selected, int total = 221, bool served = false) =>
        new("2026-08-15T18:00:00Z", "abc", full, analysis, suite, selected, total, served);

    private static readonly RunRecord[] Mixed =
    [
        Run(full: true, analysis: 5.0, suite: 20, selected: 221),
        Run(full: false, analysis: 5.0, suite: 2, selected: 20),
        Run(full: false, analysis: 5.4, suite: 2, selected: 20),
        Run(full: false, analysis: 4.6, suite: 2, selected: 20),
        Run(full: false, analysis: 0.8, suite: 2, selected: 20, served: true),
        Run(full: false, analysis: 0.9, suite: 2, selected: 20, served: true),
        Run(full: false, analysis: 1.0, suite: 2, selected: 20, served: true),
    ];

    [Fact]
    public void Served_and_in_process_analyses_are_averaged_apart()
    {
        // #97: about a second served against five in-process. The mixed average, 2.95s, describes
        // neither way of working.
        var served = RunLedger.Assess(Mixed, served: true);
        var inProcess = RunLedger.Assess(Mixed, served: false);
        var all = RunLedger.Assess(Mixed);

        Assert.Equal(0.9, served!.AnalysisSeconds, 3);
        Assert.True(served.AnalysisServed);
        Assert.Equal(5.0, inProcess!.AnalysisSeconds, 3);
        Assert.False(inProcess.AnalysisServed);
        Assert.Equal(2.95, all!.AnalysisSeconds, 3);
        Assert.Null(all.AnalysisServed);

        Assert.Equal(3, served.Served!.Runs);
        Assert.Equal(0.9, served.Served.AnalysisSeconds, 3);
        Assert.Equal(3, served.InProcess!.Runs);
        Assert.Equal(6, served.Runs);
        Assert.Equal(served.SelectedFraction, inProcess.SelectedFraction, 6);
    }

    [Fact]
    public void Too_few_runs_of_ones_own_kind_fall_back_to_all_of_them()
    {
        var records = Mixed.Take(5).ToList();

        var verdict = RunLedger.Assess(records, served: true);

        Assert.Null(verdict!.AnalysisServed);
        Assert.Equal(records.Skip(1).Average(r => r.AnalysisSeconds), verdict.AnalysisSeconds, 6);
    }

    [Fact]
    public void A_record_from_before_the_field_reads_as_in_process()
    {
        var path = Path.Combine(Path.GetTempPath(), "tia-ledger-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(RunLedger.PathIn(path),
                "{\"at\":\"2026-08-15T18:00:00Z\",\"headCommit\":\"abc\",\"fullRun\":false,\"analysisSeconds\":5,\"suiteSeconds\":2,\"selectedTests\":1,\"totalTests\":2}\n");

            Assert.False(Assert.Single(RunLedger.Read(path)).Served);

            RunLedger.Append(path, Run(full: false, analysis: 1, suite: 2, selected: 1, served: true));
            Assert.True(RunLedger.Read(path)[^1].Served);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void The_figures_show_both_populations_and_say_which_one_A_is()
    {
        var text = RunLedger.Format(RunLedger.Assess(Mixed, served: true)!);

        Assert.Contains("0.9s  served by tia server, as now", text, StringComparison.Ordinal);
        Assert.Contains("0.9s over 3 run(s)", text, StringComparison.Ordinal);
        Assert.Contains("5.0s over 3 run(s)", text, StringComparison.Ordinal);
    }

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

    /// <summary>
    /// The guard that keeps a fast suite out of two-invocation runs. Cartographer's own numbers: a
    /// 6.4 s suite, a quarter of it selected, so the tests that would run take about a second and a
    /// half. A wave cannot save more than half of that, and half of that does not buy a process
    /// start.
    /// </summary>
    [Fact]
    public void A_fast_suite_is_not_worth_a_second_invocation()
    {
        var verdict = RunLedger.Assess([
            Run(full: true, analysis: 3.1, suite: 6.4, selected: 338, total: 338),
            Run(full: false, analysis: 3.1, suite: 1.5, selected: 79, total: 338),
            Run(full: false, analysis: 3.0, suite: 1.4, selected: 76, total: 338),
        ]);

        var split = RunLedger.AssessSplit(verdict, firstWaveTests: 20, selectedTests: 79, totalTests: 338);

        Assert.False(split.Split);
        Assert.Contains("does not cover the extra invocation", split.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the case it exists for: a suite where waiting for the whole selection is the cost. This
    /// tool's own, at 95 s, with most of it selected.
    /// </summary>
    [Fact]
    public void A_slow_suite_is_worth_a_second_invocation()
    {
        var verdict = RunLedger.Assess([
            Run(full: true, analysis: 5.5, suite: 95, selected: 438, total: 438),
            Run(full: false, analysis: 5.5, suite: 60, selected: 280, total: 438),
            Run(full: false, analysis: 5.4, suite: 58, selected: 270, total: 438),
        ]);

        var split = RunLedger.AssessSplit(verdict, firstWaveTests: 70, selectedTests: 280, totalTests: 438);

        Assert.True(split.Split);
        Assert.Contains("sooner", split.Explanation, StringComparison.Ordinal);

        // The arithmetic, pinned: a 95 s suite, 280 of 438 selected, a wave of 70 replacing an
        // expected half. Invariant, because this machine is de-DE and would otherwise say "15,2s".
        Assert.Contains("15.2s", split.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a ledger there is no suite time, and without a suite time the saving is unknown.
    /// Declining costs one invocation; guessing costs one on every run of a repository that never
    /// needed it.
    /// </summary>
    [Fact]
    public void Without_a_ledger_nothing_is_divided()
    {
        Assert.False(RunLedger.AssessSplit(null, 20, 80, 300).Split);
        Assert.False(RunLedger.AssessSplit(RunLedger.Assess([Run(false, 5, 50, 100)]), 20, 80, 300).Split);
    }
}
