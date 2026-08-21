using System.CommandLine;
using Tia.Core.Infrastructure;
using Tia.Core.Reporting;
using Tia.Frameworks;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>Analyses, then invokes <c>dotnet test</c> once per project with the generated filters.</summary>
public static class RunCommand
{
    /// <summary>
    /// Why this report cannot be run, or null when it can.
    /// </summary>
    /// <remarks>
    /// A full run with no projects in it is a contradiction, and it used to be reported as
    /// success: analysis threw before the workspace loaded, the fallback report carried no
    /// projects, the "did anything get selected" filter found none, and <c>run</c> printed
    /// "nothing was impacted by this diff" and exited 0. A failure that silently runs no tests and
    /// returns green is the exact outcome the fallback exists to prevent, and it blamed the diff
    /// for a decision the failure made. It is now the one case that refuses to proceed.
    /// </remarks>
    public static string? UnrunnableFullRun(AnalysisReport report) =>
        report.IsFullRun && report.Projects.Count == 0
            ? "Analysis fell back to a full run but could not enumerate any test project, so there " +
              "is nothing to invoke. Run the suite yourself; do not treat this as a pass."
            : null;

    public static Command Create(CommonOptions common)
    {
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Print the dotnet test commands that would run, and stop.",
        };

        var failFast = new Option<bool>("--fail-fast")
        {
            Description = "Stop as soon as anything fails, instead of running the rest for a complete list.",
        };

        var noPrebuild = new Option<bool>("--no-prebuild")
        {
            Description = "Do not build while analysing; let dotnet test build as usual.",
        };

        var command = new Command("run", "Analyse, then run only the impacted tests.")
        {
            // Everything after `--` is handed to dotnet test unchanged.
            TreatUnmatchedTokensAsErrors = false,
        };

        // `run` interleaves its own output with the test runner's, so there is no stream a JSON
        // document could own. `tia analyze --json` is the machine-readable form of the same
        // analysis, and the caller decides what to do with it.
        common.AddTo(command, common.Json);
        command.Options.Add(dryRun);
        command.Options.Add(failFast);
        command.Options.Add(noPrebuild);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(common.Verbose);
            var options = common.Read(parseResult, verbose ? Console.Error.WriteLine : null);
            var passthrough = parseResult.UnmatchedTokens.ToList();

            var isDryRun = parseResult.GetValue(dryRun);

            // Started before the analysis, not after it: the point is that the two do not depend on
            // each other, so whichever is shorter is free. See BuildAhead for what disqualifies it
            // and why passing `--no-build` is the part that needs care.
            var prebuild = !parseResult.GetValue(noPrebuild) && BuildAhead.Applies(isDryRun, passthrough)
                ? BuildAhead.Start(options.SolutionPath, options.RepositoryRoot, cancellationToken)
                : null;

            var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
            var report = outcome.Report;

            Console.Out.Write(ReportRenderer.Render(report, verbose));

            // Awaited unconditionally, including when nothing was selected. A build left running is
            // a process nobody is going to stop, which this branch has already paid for once.
            if (prebuild is not null)
            {
                var built = await prebuild.ConfigureAwait(false);
                if (BuildAhead.Report(built) is { } buildFailure)
                {
                    return buildFailure;
                }

                passthrough = ["--no-build"];
            }

            return SelectiveRun.Execute(report, options, new SelectiveRunSettings(
                DryRun: isDryRun,
                FailFast: parseResult.GetValue(failFast),
                Verbose: verbose)
            {
                Passthrough = passthrough,
            }, cancellationToken);
        });

        return command;
    }
}
