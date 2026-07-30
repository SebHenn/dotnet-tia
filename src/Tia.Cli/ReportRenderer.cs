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
                output.AppendLine($"    ! {reason}");
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
            : $"{Number(report.ImpactedTests)} of {Number(report.TotalTests)}  ({report.ImpactRatio:P1})");

        if (report.SelectedTests != report.ImpactedTests)
        {
            // A project that runs unfiltered runs everything in it, so what will execute is not
            // what the graph chose. Showing only one of the two flatters or maligns the engine.
            Field(output, "Will run", $"{Number(report.SelectedTests)} of {Number(report.TotalTests)}  ({report.SelectionRatio:P1})");
        }

        if (report.Widenings.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("  Widenings");
            foreach (var widening in report.Widenings)
            {
                output.AppendLine($"    ! {widening.Cause,-16} {widening.Scope}: {widening.Detail}");
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
                output.AppendLine($"    {project.Name,-32} {project.SelectedTests,6} / {project.TotalTests,-6}  {mode}");
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
                    output.AppendLine($"    {test}");
                }
            }
        }

        if (report.Diagnostics.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("  Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                output.AppendLine($"    {diagnostic}");
            }
        }

        output.AppendLine();
        Field(output, "Elapsed", $"{report.ElapsedSeconds:0.0}s");
        output.AppendLine();

        return output.ToString();
    }

    private static void Field(StringBuilder output, string label, string value) =>
        output.AppendLine($"  {label,-20}  {value}");

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Count(int value, string noun) => $"{Number(value)} {noun}{(value == 1 ? string.Empty : "s")}";

    private static string Short(string commit) => commit.Length > 9 ? commit[..9] : commit;
}
