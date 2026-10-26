using System.Globalization;
using System.Text;
using Tia.Core.Reporting;

namespace Tia.Cli;

/// <summary>
/// The report as Markdown, for a CI job's summary page (#71).
/// </summary>
/// <remarks>
/// <para>
/// Not the text report with different punctuation. The text report is read in a terminal by the
/// person who ran the command, and prints everything; a job summary is read later, by a reviewer
/// who did not run anything and wants three answers - how much ran, why not less, and what nothing
/// tests. So the headline is the selection, the reasons and widenings follow, and what a reader
/// does not need first is folded away rather than left out.
/// </para>
/// <para>
/// Appended rather than written, because <c>$GITHUB_STEP_SUMMARY</c> is one file per step that
/// other tools in the same step may also be writing to.
/// </para>
/// <para>
/// Every list is capped (#100). GitHub drops a step summary over 1 MiB outright, and a large
/// refactor can produce thousands of untested changes, since every new method is one, so an
/// uncapped summary would vanish exactly when it had the most to say. What is left out is counted,
/// and is in the JSON report, which has no such limit.
/// </para>
/// </remarks>
public static class MarkdownSummary
{
    /// <summary>Widenings shown before the rest are folded into a details block.</summary>
    private const int VisibleWidenings = 5;

    /// <summary>Widenings listed at all, folded ones included.</summary>
    public const int MaxWidenings = 100;

    public const int MaxUntestedChanges = 50;

    /// <summary>Load failures and full-run reasons.</summary>
    public const int MaxReasons = 25;

    /// <summary>
    /// The longest a single detail may print. Details come from diagnostics, and one compiler
    /// message can carry a whole file path list.
    /// </summary>
    public const int MaxDetailLength = 500;

    /// <param name="testOutcome">What the run did, for <c>run</c>; null for <c>analyze</c>, which runs nothing.</param>
    public static string Render(AnalysisReport report, string? testOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var output = new StringBuilder();

        if (report.WorkspaceUnusable)
        {
            output.AppendLine("### dotnet-tia: the workspace did not resolve");
            output.AppendLine();
            output.AppendLine("These numbers describe a failed load, not this repository.");
            output.AppendLine();
            Bullets(output, report.LoadFailures);
        }
        else if (report.IsFullRun)
        {
            output.AppendLine(CultureInfo.InvariantCulture, $"### dotnet-tia: full run, {Formatting.Number(report.TotalTests)} tests");
            output.AppendLine();
            output.AppendLine("Selection was not applied, because:");
            output.AppendLine();
            Bullets(output, report.FullRunReasons.Except(report.LoadFailures, StringComparer.Ordinal));
        }
        else
        {
            output.AppendLine(CultureInfo.InvariantCulture,
                $"### dotnet-tia: {Formatting.Number(report.SelectedTests)} of {Formatting.Number(report.TotalTests)} tests selected ({Formatting.Percent(report.SelectionRatio)})");
            output.AppendLine();

            var changed = report.Diff.ChangedSymbolCount == 1 ? "1 changed symbol" : $"{Formatting.Number(report.Diff.ChangedSymbolCount)} changed symbols";
            output.AppendLine(CultureInfo.InvariantCulture,
                $"Against {Code(report.BaseRef)}{(report.BaseCommit is { } commit ? $" ({Code(Formatting.Short(commit))})" : string.Empty)}: " +
                $"{changed} in {Formatting.Number(report.Diff.FileCount)} file(s). The graph reached {Formatting.Number(report.ImpactedTests)} test(s).");
        }

        if (report.Shard is { } shard)
        {
            output.AppendLine();
            output.AppendLine(CultureInfo.InvariantCulture,
                $"Shard {shard.Index} of {shard.Count}: this job runs {Formatting.Number(report.SelectedTests)} of the {Formatting.Number(shard.SelectedAcrossShards)} tests the shards run between them.");
        }

        if (testOutcome is not null)
        {
            output.AppendLine();
            output.AppendLine(CultureInfo.InvariantCulture, $"**{Escape(testOutcome)}**");
        }

        if (report.Projects.Count > 0 || report.UnanalysedTestProjects.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("| Project | Selected | Total | How it runs |");
            output.AppendLine("|---|---:|---:|---|");
            foreach (var project in report.Projects)
            {
                var mode = project.Filtered
                    ? project.FilterInFile ? "filtered, filter passed in a file" : "filtered"
                    : $"whole: {project.UnfilteredReason}";
                output.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Escape(project.Name)} | {Formatting.Number(project.SelectedTests)} | {Formatting.Number(project.TotalTests)} | {Escape(mode)} |");
            }

            foreach (var project in report.UnanalysedTestProjects)
            {
                var mode = project.Runs ? $"whole: {project.Reason}" : $"does not run: {project.Reason}";
                output.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Escape(project.Name)} | ? | ? | {Escape($"{project.Language}, not analysed; {mode}")} |");
            }
        }

        if (report.UntestedChanges.Count > 0 || report.UncheckedChanges > 0)
        {
            output.AppendLine();
            output.AppendLine("#### Changed code no test reaches");
            output.AppendLine();
            foreach (var change in report.UntestedChanges.Take(MaxUntestedChanges))
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"- {Code(Clip(change.Symbol))} in {Code(Clip(change.File ?? change.Project))}");
            }

            More(output, report.UntestedChanges.Count - MaxUntestedChanges, " untested change(s)");

            if (report.UncheckedChanges > 0)
            {
                output.AppendLine(CultureInfo.InvariantCulture,
                    $"- {Formatting.Number(report.UncheckedChanges)} more changed symbol(s) not checked; `--fail-on-untested` checks them all");
            }
        }

        if (report.Widenings.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("#### Widenings");
            output.AppendLine();
            foreach (var widening in report.Widenings.Take(VisibleWidenings))
            {
                output.AppendLine(Widening(widening));
            }

            if (report.Widenings.Count > VisibleWidenings)
            {
                output.AppendLine();
                output.AppendLine(CultureInfo.InvariantCulture, $"<details><summary>{report.Widenings.Count - VisibleWidenings} more</summary>");
                output.AppendLine();
                foreach (var widening in report.Widenings.Skip(VisibleWidenings).Take(MaxWidenings - VisibleWidenings))
                {
                    output.AppendLine(Widening(widening));
                }

                More(output, report.Widenings.Count - MaxWidenings, " widening(s)");

                output.AppendLine();
                output.AppendLine("</details>");
            }
        }

        if (!report.WorkspaceUnusable)
        {
            output.AppendLine();
            // A full run was a decision about the diff, not a selection that failed to pay, so it
            // gets no break-even.
            output.AppendLine(FormattableString.Invariant($"Analysis took {report.ElapsedSeconds:0.0} s.") +
                (report.IsFullRun
                    ? string.Empty
                    : report.BreakEvenSuiteSeconds is { } breakEven
                        ? FormattableString.Invariant($" Selecting pays off when the full suite takes more than {breakEven:0} s.")
                        : " This diff selects the whole suite, so selecting cannot pay off on it."));
        }

        output.AppendLine();
        return output.ToString();
    }

    /// <summary>Appends to <paramref name="path"/>, creating it if need be.</summary>
    public static void AppendTo(string path, AnalysisReport report, string? testOutcome = null) =>
        File.AppendAllText(path, Render(report, testOutcome), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string Widening(WideningEvent widening) =>
        $"- **{Escape(widening.Cause)}** {Code(Clip(widening.Scope))}: {Escape(Clip(widening.Detail))}";

    private static void Bullets(StringBuilder output, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        foreach (var line in all.Take(MaxReasons))
        {
            output.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(Clip(line))}");
        }

        More(output, all.Count - MaxReasons, string.Empty);
    }

    /// <summary>The line that stands for what a capped list left out.</summary>
    private static void More(StringBuilder output, int omitted, string what)
    {
        if (omitted > 0)
        {
            output.AppendLine(CultureInfo.InvariantCulture,
                $"- ...and {Formatting.Number(omitted)} more{what}; the full list is in the JSON report (`--report <file>`)");
        }
    }

    private static string Clip(string text) =>
        text.Length <= MaxDetailLength ? text : string.Concat(text.AsSpan(0, MaxDetailLength), "...");

    /// <summary>
    /// A code span that survives whatever is inside it. A symbol can hold a backtick - a generic
    /// type's metadata name does - so the fence is one longer than the longest run of them.
    /// </summary>
    public static string Code(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', longest + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : string.Empty;
        return fence + pad + text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ') + pad + fence;
    }

    /// <summary>
    /// Plain text with the characters that would start Markdown or HTML neutralised. The details
    /// come from file names and diagnostics, so they are not ours to trust as markup.
    /// </summary>
    public static string Escape(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '#' or '!' or '~')
            {
                output.Append('\\');
            }

            output.Append(c is '\r' or '\n' ? ' ' : c);
        }

        return output.ToString();
    }
}
