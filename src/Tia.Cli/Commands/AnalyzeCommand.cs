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

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(common.Verbose);
            var json = parseResult.GetValue(common.Json);
            var options = common.Read(parseResult, verbose && !json ? Console.Error.WriteLine : null);

            var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);

            Console.Out.Write(json
                ? outcome.Report.ToJson() + Environment.NewLine
                : ReportRenderer.Render(outcome.Report, verbose));

            return 0;
        });

        return command;
    }
}
