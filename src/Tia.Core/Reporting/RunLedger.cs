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
/// Append-only JSONL, capped, and safe to delete: it informs a message and nothing else. No
/// selection decision reads it - a ledger that changed what ran would be a correctness surface, and
/// this is a reporting one.
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
        if (usable.Count < 3)
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
