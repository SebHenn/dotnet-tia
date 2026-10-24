using Tia.Core.Reporting;

namespace Tia.Cli;

/// <summary>The <c>--fail-on-untested</c> gate shared by <c>analyze</c> and <c>run</c> (#70).</summary>
public static class Untested
{
    /// <summary>The exit code the gate decides, or null when it does not apply or passes.</summary>
    /// <remarks>
    /// A full run passes it, and says so: nothing was traversed, so there is no answer to gate on,
    /// and the default branch - where every run is a full one - is not where this question is asked.
    /// Saying it is the difference between a gate that passed and a gate that never looked.
    /// </remarks>
    public static int? Gate(AnalysisReport report, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!enabled)
        {
            return null;
        }

        if (report.IsFullRun)
        {
            Console.Error.WriteLine("  --fail-on-untested: not checked, because this is a full run.");
            return null;
        }

        if (report.UntestedChanges.Count == 0)
        {
            return null;
        }

        Console.Error.WriteLine(report.UntestedChanges.Count == 1
            ? "  --fail-on-untested: 1 changed symbol is reached by no test."
            : $"  --fail-on-untested: {report.UntestedChanges.Count} changed symbols are reached by no test.");
        return ExitCodes.Failed;
    }
}
