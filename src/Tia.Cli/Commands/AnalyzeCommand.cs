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

        command.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(common.Verbose);
            var json = parseResult.GetValue(common.Json);
            var options = common.Read(parseResult, verbose && !json ? Console.Error.WriteLine : null);

            var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);

            Console.Out.Write(json
                ? outcome.Report.ToJson() + Environment.NewLine
                : ReportRenderer.Render(outcome.Report, verbose));

            // A full run still exits 0: it is a decision, and this command reports decisions. A
            // load that resolved no tests is not one - the report it produced describes a failed
            // load rather than this repository - so it is the one state that fails the command.
            return outcome.Report.WorkspaceUnusable ? ExitCodes.Inconclusive : ExitCodes.Ok;
        }));

        return command;
    }
}
