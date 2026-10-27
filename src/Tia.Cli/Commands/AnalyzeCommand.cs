using System.CommandLine;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>
/// Prints the impacted tests without running anything. This is the primary CI integration point:
/// a job can read the JSON and decide what to do with it.
/// </summary>
public static class AnalyzeCommand
{
    public static Command Create(CommonOptions common)
    {
        var command = new Command("analyze", "Print the tests a diff can affect. Runs nothing.");
        common.AddTo(command);
        foreach (var option in common.Selection)
        {
            command.Options.Add(option);
        }

        command.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(common.Verbose);
            var json = parseResult.GetValue(common.Json);
            var options = common.Read(parseResult, verbose && !json ? Console.Error.WriteLine : null);

            var outcome = await common.AnalyzeAsync(parseResult, options, options.Log, cancellationToken).ConfigureAwait(false);
            outcome = outcome with { Report = common.ApplyShard(parseResult, outcome, options) };

            Console.Out.Write(json
                ? outcome.Report.ToJson() + Environment.NewLine
                : ReportRenderer.Render(outcome.Report, verbose));

            if (parseResult.GetValue(common.Summary) is { Length: > 0 } summary)
            {
                MarkdownSummary.AppendTo(summary, outcome.Report);
                Annotations.AttachSummary(summary, json ? Console.Error : Console.Out);
            }

            common.WriteReport(parseResult, outcome.Report);
            common.WriteCodeQuality(parseResult, outcome.Report);

            // To stderr under --json, which keeps stdout one JSON document; the runner reads
            // workflow commands from both.
            if (parseResult.GetValue(common.Annotations))
            {
                Annotations.Write(outcome.Report, parseResult.GetValue(common.FailOnUntested), json ? Console.Error : Console.Out);
            }

            // A full run still exits 0: it is a decision, and this command reports decisions. A
            // load that resolved no tests is not one - the report it produced describes a failed
            // load rather than this repository - so it is the one state that fails the command.
            if (outcome.Report.WorkspaceUnusable)
            {
                return ExitCodes.Inconclusive;
            }

            return Untested.Gate(outcome.Report, parseResult.GetValue(common.FailOnUntested)) ?? ExitCodes.Ok;
        }));

        return command;
    }
}
