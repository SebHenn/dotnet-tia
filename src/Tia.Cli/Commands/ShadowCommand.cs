using System.CommandLine;
using System.Text.Json;
using Tia.Core.Reporting;
using Tia.Workspace.Harness;

namespace Tia.Cli.Commands;

/// <summary>
/// Selects, then runs the whole suite anyway, and reports which failures the selection would have
/// skipped. The way a repository decides for itself whether selection is safe on <em>its</em> code.
/// </summary>
public static class ShadowCommand
{
    public static Command Create(CommonOptions common)
    {
        var command = new Command(
            "shadow",
            "Run the full suite and report which failures a selection would have skipped.");

        // No --full and no --default-branch: both force a full run, which is precisely the state in
        // which shadow mode has nothing to observe. Offering them would mean accepting an
        // invocation that can only ever answer "inconclusive".
        common.AddTo(command, common.Full, common.DefaultBranch);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(common.Json);
            var verbose = parseResult.GetValue(common.Verbose);
            var options = common.Read(parseResult, verbose && !json ? Console.Error.WriteLine : null);

            var result = await new ShadowRunner(options, json ? null : m => Console.Out.WriteLine("  " + m))
                .RunAsync(cancellationToken)
                .ConfigureAwait(false);

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        Verdict = result.Verdict.ToString(),
                        result.Misses,
                        result.FailingTests,
                        result.Unobserved,
                        result.InconclusiveReason,
                        SelectedFraction = Math.Round(result.SelectedFraction, 4),
                        result.Report.SelectedTests,
                        result.Report.ImpactedTests,
                        result.Report.TotalTests,
                        Report = result.Report,
                    },
                    AnalysisReport.JsonOptions));

                return ExitCode(result.Verdict);
            }

            Console.Out.Write(ReportRenderer.Render(result.Report, verbose));

            switch (result.Verdict)
            {
                case ShadowVerdict.Clean:
                    Console.Out.WriteLine(FormattableString.Invariant(
                        $"  Shadow                {result.FailingTests} failing test(s), all of them selected"));
                    Console.Out.WriteLine(WouldHaveRun(result));
                    Console.Out.WriteLine();
                    Console.Out.WriteLine("  SAFE - every test that failed was in the selection.");
                    break;

                case ShadowVerdict.NoFailures:
                    Console.Out.WriteLine(WouldHaveRun(result));
                    Console.Out.WriteLine();
                    Console.Out.WriteLine("  NOTHING AT RISK - the suite was green, so no failure existed to be missed.");
                    Console.Out.WriteLine("  This says nothing about whether the selection is safe; only a red run can.");
                    break;

                case ShadowVerdict.Miss:
                    Console.Out.WriteLine(FormattableString.Invariant(
                        $"  Shadow                {result.FailingTests} failing test(s), {result.Misses.Count} NOT selected"));
                    Console.Out.WriteLine();
                    Console.Out.WriteLine("  MISS - selecting would have skipped a test that failed:");
                    foreach (var miss in result.Misses.Take(20))
                    {
                        Console.Out.WriteLine($"      - {miss}");
                    }

                    if (result.Misses.Count > 20)
                    {
                        Console.Out.WriteLine($"      ... and {result.Misses.Count - 20} more");
                    }

                    Console.Out.WriteLine();
                    Console.Out.WriteLine("  Run `tia explain <TestName>` on one of these: it prints the near misses, which is");
                    Console.Out.WriteLine("  usually enough to see which edge the graph is lacking.");
                    break;

                default:
                    Console.Out.WriteLine("  INCONCLUSIVE - " + result.InconclusiveReason);
                    break;
            }

            Console.Out.WriteLine();
            return ExitCode(result.Verdict);
        });

        return command;
    }

    /// <summary>
    /// Distinct codes, because the three answers call for different responses and a caller that
    /// cannot tell "safe" from "could not tell" is back to guessing. A repository still gathering
    /// evidence should run this with <c>continue-on-error</c> and read the codes, rather than have
    /// the tool decide for it whether a miss ought to break the build.
    /// </summary>
    private static int ExitCode(ShadowVerdict verdict) => verdict switch
    {
        ShadowVerdict.Clean or ShadowVerdict.NoFailures => 0,
        ShadowVerdict.Miss => 1,
        _ => 2,
    };

    private static string WouldHaveRun(ShadowResult result) => FormattableString.Invariant(
        $"  Would have run        {result.Report.SelectedTests} of {result.Report.TotalTests}  ({result.SelectedFraction * 100:0.0} %)");
}
