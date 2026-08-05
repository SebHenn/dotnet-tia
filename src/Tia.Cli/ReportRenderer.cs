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

        if (report.IsFullRun)
        {
            output.AppendLine("  FULL RUN - selection was not applied");
            output.AppendLine();
            foreach (var reason in report.FullRunReasons)
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"    ! {reason}");
            }

            output.AppendLine();
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
                output.AppendLine(CultureInfo.InvariantCulture, $"    {project.Name,-32} {project.SelectedTests,6} / {project.TotalTests,-6}  {mode}");
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
        Field(output, "Worth it if", report.BreakEvenSuiteSeconds is { } breakEven
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
