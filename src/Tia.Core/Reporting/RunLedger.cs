using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tia.Core.Reporting;

/// <summary>One completed <c>run</c>: what the analysis cost, what it skipped, and what it saved.</summary>
/// <param name="SuiteSeconds">
/// How long the tests themselves took. On a full run this is <c>T</c> - the thing the break-even is
/// measured against, and the only way to observe it. On a selective run it is <c>fT</c>, which
/// cannot be turned back into <c>T</c> without knowing <c>f</c>, which is why both are recorded.
/// </param>
public sealed record RunRecord(
    string At,
    string HeadCommit,
    bool FullRun,
    double AnalysisSeconds,
    double SuiteSeconds,
    int SelectedTests,
    int TotalTests);

/// <summary>
/// What selection has actually cost or saved on this repository, from runs that happened.
/// </summary>
/// <remarks>
/// <para>
/// Every term of the break-even was already measured except the one that decides it. A selective run
/// costs <c>A + fT</c> against a full run's <c>T</c>, and the tool measured <c>A</c> and <c>f</c> on
/// every run while never once timing the suite it had just spawned. So it could print
/// <i>"worth it if the full suite takes more than 14s"</i> without ever noticing that the suite
/// takes two.
/// </para>
/// <para>
/// That is not a missing feature, it is the tool being unable to check its own central claim. The
/// author of this tool discovered it did not pay off on his own application by working the
/// arithmetic out by hand in a design document, and then left the CI job commented out. This exists
/// so the next person is told instead.
/// </para>
/// <para>
/// Append-only JSONL, capped, and safe to delete. <b>No selection decision reads it</b> - a ledger
/// that changed which tests ran would be a correctness surface, and this is a reporting one.
/// <see cref="AssessSplit"/> is the one thing here that changes what a command does, and it is
/// deliberately on the other side of that line: it decides how many invocations the selected tests
/// are handed to, never which tests they are. A missing or nonsense ledger costs one invocation,
/// not a missed test.
/// </para>
/// </remarks>
public static class RunLedger
{
    /// <summary>
    /// Kept small on purpose. This answers "is it paying off lately", and a year of history would
    /// average away the answer - a repository whose suite has grown should be judged on the runs
    /// since it grew.
    /// </summary>
    public const int Capacity = 100;

    public const string FileName = "runs.jsonl";

    /// <summary>
    /// How many runs it takes before the ledger will say anything. One run is an anecdote, and the
    /// figures it would produce - a cold analysis, a first-build suite - are the least
    /// representative ones this file will ever hold.
    /// </summary>
    public const int MinimumRuns = 3;

    /// <summary>
    /// One record per line, so appending never rewrites what is already there and a killed run
    /// costs its own line rather than the file. <see cref="AnalysisReport.JsonOptions"/> indents,
    /// which JSONL cannot.
    /// </summary>
    private static readonly JsonSerializerOptions LineOptions = new(AnalysisReport.JsonOptions)
    {
        WriteIndented = false,
    };

    public static string PathIn(string cacheDirectory) => Path.Combine(cacheDirectory, FileName);

    /// <summary>
    /// Appends a record, trimming to <see cref="Capacity"/>. Never throws: a run that produced a
    /// verdict must not fail because a note about it could not be filed.
    /// </summary>
    public static void Append(string cacheDirectory, RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var path = PathIn(cacheDirectory);
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            lines.Add(JsonSerializer.Serialize(record, LineOptions));

            if (lines.Count > Capacity)
            {
                lines.RemoveRange(0, lines.Count - Capacity);
            }

            File.WriteAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Deliberately silent. The alternative is a warning on every run in a container with a
            // read-only mount, about a file whose only purpose is advice.
        }
    }

    public static IReadOnlyList<RunRecord> Read(string cacheDirectory)
    {
        var path = PathIn(cacheDirectory);
        if (!File.Exists(path))
        {
            return [];
        }

        var records = new List<RunRecord>();

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                // A half-written last line from a killed run should cost that line, not the file.
                try
                {
                    if (JsonSerializer.Deserialize<RunRecord>(line, LineOptions) is { } record)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return records;
    }

    /// <summary>
    /// What the ledger says selection is worth here, or null when it cannot say yet.
    /// </summary>
    public static LedgerVerdict? Assess(IReadOnlyList<RunRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var usable = records.Where(r => r.TotalTests > 0 && r.SuiteSeconds > 0).ToList();
        if (usable.Count < MinimumRuns)
        {
            return null;
        }

        // T comes from full runs, because only a full run observes it. Without one, a selective run
        // whose suite took fT still bounds T from below - it is at least what already ran - and a
        // lower bound on T is the conservative direction: it understates the saving rather than
        // inventing one.
        var fullRuns = usable.Where(r => r.FullRun).ToList();
        var suiteSeconds = fullRuns.Count > 0
            ? fullRuns.Average(r => r.SuiteSeconds)
            : usable.Max(r => r.SuiteSeconds);

        var selective = usable.Where(r => !r.FullRun).ToList();
        if (selective.Count == 0)
        {
            return null;
        }

        var analysis = selective.Average(r => r.AnalysisSeconds);
        var fraction = selective.Average(r => (double)r.SelectedTests / r.TotalTests);

        return new LedgerVerdict(
            Runs: selective.Count,
            AnalysisSeconds: analysis,
            SelectedFraction: fraction,
            SuiteSeconds: suiteSeconds,
            SuiteObserved: fullRuns.Count > 0);
    }

    /// <summary>
    /// A sentence for a reader who is losing time, or null when they are not.
    /// </summary>
    /// <remarks>
    /// Only ever printed against the tool's own interest. A tool that quietly costs the time it
    /// claims to save is the over-claiming this repository's honesty section exists to prevent, and
    /// saying nothing would make this file a way of knowing without telling.
    /// </remarks>
    public static string? Advice(LedgerVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (verdict.NetSecondsPerRun >= 0)
        {
            return null;
        }

        var qualifier = verdict.SuiteObserved
            ? string.Empty
            : " (estimated from selective runs - no full run has been observed, so this understates the loss)";

        return string.Format(
            CultureInfo.InvariantCulture,
            "selection is costing you time on this repository{0}: over the last {1} selective run(s), " +
            "analysis took {2:0.0}s and selection ran {3:0}% of a {4:0.0}s suite, which is {5:0.0}s " +
            "worse per run than simply running everything. Your suite is too fast for selection to " +
            "pay. Consider not enabling it, and re-checking when the suite grows.",
            qualifier,
            verdict.Runs,
            verdict.AnalysisSeconds,
            verdict.SelectedFraction * 100,
            verdict.SuiteSeconds,
            -verdict.NetSecondsPerRun);
    }

    /// <summary>
    /// What one more <c>dotnet test</c> costs before a single test runs: process start, the SDK's
    /// up-to-date check, and the runner's discovery pass.
    /// </summary>
    /// <remarks>
    /// An estimate, and openly one. It is dominated by machinery outside this tool, so measuring it
    /// per repository would mean timing a phase this tool does not own. <see cref="SplitMargin"/>
    /// is what makes that acceptable: the projected saving has to clear the estimate several times
    /// over, so being wrong about it by a factor of two changes nothing.
    /// </remarks>
    public const double ExtraInvocationSeconds = 1.5;

    /// <summary>How many times the extra invocation the projected saving must be worth.</summary>
    public const double SplitMargin = 3.0;

    /// <summary>
    /// Whether running the nearest tests as their own invocation is worth what the invocation
    /// costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trade is asymmetric and the arithmetic should not pretend otherwise. The extra
    /// invocation is paid on <b>every</b> run; the saving lands only on a run that fails, and only
    /// when the failure is in the wave. So this is not a total-time optimisation - it buys latency
    /// to the first failure, and on a fast suite it is not worth buying. Requiring the saving to
    /// clear <see cref="SplitMargin"/> times the cost is what keeps a fast suite out of it.
    /// </para>
    /// <para>
    /// Against an unsplit run the expected position of a first failure is half the selection: no
    /// runner promises an order, so there is nothing better to assume. The wave replaces that with
    /// its own size.
    /// </para>
    /// <para>
    /// Per-test cost is taken as uniform across the repository, which is how the project's share of
    /// the suite is estimated from a repository-wide <c>T</c>. When <c>T</c> came from selective
    /// runs only it is a lower bound, which understates the saving - the conservative direction,
    /// and the direction that declines to split rather than splitting on a guess.
    /// </para>
    /// </remarks>
    public static SplitVerdict AssessSplit(LedgerVerdict? verdict, int firstWaveTests, int selectedTests, int totalTests)
    {
        if (verdict is null)
        {
            return new SplitVerdict(false,
                $"no ledger yet - {MinimumRuns} runs are needed before the suite's cost is known");
        }

        if (firstWaveTests <= 0 || selectedTests <= 0 || totalTests <= 0)
        {
            return new SplitVerdict(false, "nothing to divide");
        }

        var selectionSeconds = verdict.SuiteSeconds * selectedTests / totalTests;
        var saving = (0.5 - ((double)firstWaveTests / selectedTests)) * selectionSeconds;
        var threshold = SplitMargin * ExtraInvocationSeconds;

        return saving >= threshold
            ? new SplitVerdict(true, FormattableString.Invariant(
                $"a failure among them should surface about {saving:0.0}s sooner, against about {ExtraInvocationSeconds:0.0}s for the extra invocation"))
            : new SplitVerdict(false, FormattableString.Invariant(
                $"running the nearest {firstWaveTests} of {selectedTests} first would surface a failure only about {Math.Max(0, saving):0.0}s sooner, which does not cover the extra invocation"));
    }

    public static string Format(LedgerVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        // Invariant throughout, like the rest of the reporting. This machine is de-DE, so a plain
        // interpolation prints "6,8s" beside the advice sentence's "6.8s" - two spellings of one
        // number in adjacent lines of the same output.
        var report = new StringBuilder();
        Line(report, "Selective runs", verdict.Runs.ToString(CultureInfo.InvariantCulture));
        Line(report, "Analysis (A)", FormattableString.Invariant($"{verdict.AnalysisSeconds:0.0}s"));
        Line(report, "Selected (f)", FormattableString.Invariant($"{verdict.SelectedFraction * 100:0}%"));
        Line(report, "Full suite (T)", verdict.SuiteObserved
            ? FormattableString.Invariant($"{verdict.SuiteSeconds:0.0}s")
            : FormattableString.Invariant($"at least {verdict.SuiteSeconds:0.0}s - no full run observed"));
        Line(report, "Selective run", FormattableString.Invariant($"{verdict.SelectiveSeconds:0.0}s  (A + fT)"));
        Line(report, "Net per run", verdict.NetSecondsPerRun >= 0
            ? FormattableString.Invariant($"saves {verdict.NetSecondsPerRun:0.0}s")
            : FormattableString.Invariant($"costs {-verdict.NetSecondsPerRun:0.0}s more than running everything"));

        return report.ToString();

        static void Line(StringBuilder output, string label, string value) =>
            output.Append("  ").Append(label.PadRight(16)).Append(value).AppendLine();
    }
}

/// <summary>Whether to divide a project's selection across two invocations, and the reason either way.</summary>
public sealed record SplitVerdict(bool Split, string Explanation);

/// <param name="SuiteObserved">
/// Whether <see cref="SuiteSeconds"/> came from a full run. When it did not it is a lower bound, and
/// every figure derived from it understates the loss rather than overstating the saving.
/// </param>
public sealed record LedgerVerdict(
    int Runs,
    double AnalysisSeconds,
    double SelectedFraction,
    double SuiteSeconds,
    bool SuiteObserved)
{
    /// <summary>What a selective run costs: <c>A + fT</c>.</summary>
    public double SelectiveSeconds => AnalysisSeconds + (SelectedFraction * SuiteSeconds);

    /// <summary>Positive is a saving, negative is a loss.</summary>
    public double NetSecondsPerRun => SuiteSeconds - SelectiveSeconds;
}
