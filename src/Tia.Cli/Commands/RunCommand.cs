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

        var command = new Command("run", "Analyse, then run only the impacted tests.")
        {
            // Everything after `--` is handed to dotnet test unchanged.
            TreatUnmatchedTokensAsErrors = false,
        };

        common.AddTo(command);
        command.Options.Add(dryRun);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(common.Verbose);
            var options = common.Read(parseResult, verbose ? Console.Error.WriteLine : null);
            var passthrough = parseResult.UnmatchedTokens.ToList();

            var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
            var report = outcome.Report;

            Console.Out.Write(ReportRenderer.Render(report, verbose));

            if (UnrunnableFullRun(report) is { } refusal)
            {
                Console.Error.WriteLine($"  {refusal}");
                Console.Error.WriteLine();
                return 1;
            }

            var projects = report.Projects.Where(p => p.SelectedTests > 0).ToList();
            if (projects.Count == 0)
            {
                Console.Out.WriteLine("  Nothing to run - no test was impacted by this diff.");
                Console.Out.WriteLine();
                return 0;
            }

            var exitCode = 0;
            var mode = TestCommandBuilder.ModeOf(report);

            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var arguments = TestCommandBuilder.Build(project, mode, passthrough);
                Console.Out.WriteLine($"  > {TestCommandBuilder.Describe(arguments)}");
                Console.Out.WriteLine();

                if (parseResult.GetValue(dryRun))
                {
                    continue;
                }

                var projectExit = ProcessRunner.RunStreaming(
                    "dotnet",
                    arguments,
                    options.RepositoryRoot,
                    Console.Out.WriteLine,
                    Console.Error.WriteLine,
                    cancellationToken);

                // The first failure, not the last. docs/usage.md has always documented "the exit
                // code of the first failing dotnet test" while the loop overwrote it with each
                // later failure - so a run whose first project failed with a distinctive code
                // reported whatever the last one happened to return.
                if (projectExit != 0 && exitCode == 0)
                {
                    exitCode = projectExit;
                }
            }

            return exitCode;
        });

        return command;
    }
}
