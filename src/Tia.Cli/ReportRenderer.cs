using System.Globalization;
using System.Text;
using Tia.Core.Reporting;

namespace Tia.Cli;

/// <summary>
/// Human-readable rendering of an analysis. Every widening and every bail-out is printed:
/// conservatism that is not reported is indistinguishable from a bug.
/// </summary>
public static class ReportRenderer
{
    public static string Render(AnalysisReport report, bool verbose)
    {
        var output = new StringBuilder();
        output.AppendLine();

        // Before the full-run block, and phrased as a refusal rather than a decision. A load that
        // resolved no tests forces a full run too, so it would otherwise arrive as one more bullet
        // in a list of ordinary conservatism - which is exactly how "0 tests discovered" came to be
        // read as a fact about a repository instead of a fact about a failed load.
        if (report.WorkspaceUnusable)
        {
            output.AppendLine("  WORKSPACE DID NOT RESOLVE - these numbers describe a failed load, not this repository");
            output.AppendLine();
            foreach (var failure in report.LoadFailures)
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"    x {failure}");
            }

            output.AppendLine();
        }

        if (report.IsFullRun)
        {
            output.AppendLine("  FULL RUN - selection was not applied");
            output.AppendLine();

            // Load failures are a subset of the reasons, and printing them twice under two
            // headings reads as two problems.
            var reasons = report.FullRunReasons.Except(report.LoadFailures, StringComparer.Ordinal).ToList();
            foreach (var reason in reasons)
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"    ! {reason}");
            }

            if (reasons.Count == 0)
            {
                output.AppendLine("    ! the workspace did not resolve, so there was nothing to select from");
            }

            output.AppendLine();
        }

        // First, because every number below is a number about this solution's test projects. When
        // discovery chose it, the choice used to show only under --verbose (issue #37).
        if (report.Solution is { } solution)
        {
            Field(output, "Solution", solution);
        }

        Field(output, "Base", report.BaseCommit is null
            ? report.BaseRef
            : $"{report.BaseRef} ({Short(report.BaseCommit)})");

        Field(output, "Diff", $"{Count(report.Diff.FileCount, "file")} ({report.Diff.CSharpFileCount} C#), " +
                              $"{Count(report.Diff.ChangedSymbolCount, "symbol")} changed");

        var cacheNote = report.Graph.ProjectsReused > 0
            ? $"  ({report.Graph.ProjectsRebuilt} rebuilt, {report.Graph.ProjectsReused} reused from cache)"
            : $"  ({report.Graph.ProjectsRebuilt} projects built)";

        Field(output, "Graph", $"{Number(report.Graph.Types)} types / {Number(report.Graph.Members)} members / " +
                               $"{Number(report.Graph.Edges)} edges{cacheNote}");

        Field(output, "Impacted tests", report.TotalTests == 0
            ? "0"
            : $"{Number(report.ImpactedTests)} of {Number(report.TotalTests)}  ({Percent(report.ImpactRatio)})");

        if (report.SelectedTests != report.ImpactedTests)
        {
            // A project that runs unfiltered runs everything in it, so what will execute is not
            // what the graph chose. Showing only one of the two flatters or maligns the engine.
            Field(output, "Will run", $"{Number(report.SelectedTests)} of {Number(report.TotalTests)}  ({Percent(report.SelectionRatio)})");
        }

        if (report.Widenings.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("  Widenings");
            foreach (var widening in report.Widenings)
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"    ! {widening.Cause,-16} {widening.Scope}: {widening.Detail}");
            }
        }

        if (report.Projects.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("  Projects");
            foreach (var project in report.Projects)
            {
                var mode = project.Filtered
                    ? $"filtered ({project.Framework}/{project.Runner})"
                    : $"unfiltered - {project.UnfilteredReason}";

                // "can run" rather than "runs": dividing the selection is safe here, but whether it
                // is worth an extra invocation is decided by `run` against the ledger, and this
                // same line is printed by `analyze`, which spawns nothing and cannot know.
                if (project.FirstWave is { } wave)
                {
                    mode += $", can run its nearest {wave.TestCount} first";
                }

                output.AppendLine(CultureInfo.InvariantCulture, $"    {project.Name,-32} {project.SelectedTests,6} / {project.TotalTests,-6}  {mode}");

                if (verbose && project.UnsplitReason is { } unsplit)
                {
                    output.AppendLine(CultureInfo.InvariantCulture, $"      no first wave: {unsplit}");
                }
            }
        }

        if (verbose && report.Projects.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("  Selected tests");
            foreach (var project in report.Projects)
            {
                foreach (var test in project.Tests)
                {
                    output.AppendLine(CultureInfo.InvariantCulture, $"    {test}");
                }
            }
        }

        if (report.Diagnostics.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("  Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"    {diagnostic}");
            }
        }

        output.AppendLine();
        Field(output, "Elapsed", FormattableString.Invariant($"{report.ElapsedSeconds:0.0}s"));

        // Analysis is not free. Stating the break-even converts a selection ratio into the
        // question an adopter actually has, and hiding it is how these tools lose trust.
        //
        // Withheld when the load failed, because the arithmetic is division by a suite that was
        // never seen: a workspace resolving no tests selects 0 of 0, which reads as a perfect
        // ratio and produces a confident, meaningless break-even.
        Field(output, "Worth it if", report.WorkspaceUnusable
            ? "unanswerable - no suite was resolved to compare against"
            : report.BreakEvenSuiteSeconds is { } breakEven
                ? $"the full suite takes more than {Duration(breakEven)}"
                : "never - this diff selects the whole suite");

        output.AppendLine();

        return output.ToString();
    }

    private static void Field(StringBuilder output, string label, string value) =>
        output.AppendLine(CultureInfo.InvariantCulture, $"  {label,-20}  {value}");

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Count(int value, string noun) => $"{Number(value)} {noun}{(value == 1 ? string.Empty : "s")}";

    private static string Short(string commit) => commit.Length > 9 ? commit[..9] : commit;

    private static string Duration(double seconds) => seconds < 90
        ? FormattableString.Invariant($"{seconds:0}s")
        : FormattableString.Invariant($"{seconds / 60:0.#} minutes");

    /// <summary>
    /// Invariant, like every other number here. The counts were already pinned and the
    /// percentages were not, so on a runner whose culture uses a decimal comma one line read
    /// "1,204 of 3,730 (8,2 %)" - two conventions in the same sentence. Tool output is a format
    /// other programs and humans read across machines; it should not move with the locale.
    /// </summary>
    private static string Percent(double ratio) => FormattableString.Invariant($"{ratio:P1}");
}
