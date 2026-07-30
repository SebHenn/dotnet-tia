using System.CommandLine;
using Tia.Core.Infrastructure;
using Tia.Frameworks;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>Analyses, then invokes <c>dotnet test</c> once per project with the generated filters.</summary>
public static class RunCommand
{
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

                if (projectExit != 0)
                {
                    exitCode = projectExit;
                }
            }

            return exitCode;
        });

        return command;
    }
}
