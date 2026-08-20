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

            var cacheDirectory = Path.IsPathRooted(options.CacheDirectory)
                ? options.CacheDirectory
                : Path.Combine(options.RepositoryRoot, options.CacheDirectory);

            // Read once, before anything runs: what the suite has cost on this repository is what
            // decides whether a second invocation per project is worth its own start-up.
            var ledger = options.UseCache ? RunLedger.Assess(RunLedger.Read(cacheDirectory)) : null;

            var exitCode = 0;
            var mode = TestCommandBuilder.ModeOf(report);
            var suiteStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var stopEarly = parseResult.GetValue(failFast);
            var isDryRun = parseResult.GetValue(dryRun);
            var ran = false;
            var abandoned = false;

            int Invoke(IReadOnlyList<string> arguments)
            {
                Console.Out.WriteLine($"  > {TestCommandBuilder.Describe(arguments)}");
                Console.Out.WriteLine();

                if (isDryRun)
                {
                    return 0;
                }

                ran = true;
                return ProcessRunner.RunStreaming(
                    "dotnet",
                    arguments,
                    options.RepositoryRoot,
                    Console.Out.WriteLine,
                    Console.Error.WriteLine,
                    cancellationToken);
            }

            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var split = project.FirstWave is { } candidate
                    ? RunLedger.AssessSplit(ledger, candidate.TestCount, project.SelectedTests, report.TotalTests)
                    : null;

                if (verbose && split is { Split: false })
                {
                    Console.Out.WriteLine($"  {project.Name} runs in one invocation: {split.Explanation}");
                }

                // Two invocations, nearest tests first. The remainder still runs by default: the
                // wave buys the developer the failure in seconds, and stopping there would take
                // away the complete list that a pull request needs. `--fail-fast` is for when the
                // first answer is the only one wanted.
                var divided = split is { Split: true } ? project.FirstWave : null;

                List<IReadOnlyList<string>> waves = divided is null
                    ? [project.FilterArguments]
                    : [divided.FilterArguments, divided.RemainderFilterArguments];

                if (divided is not null)
                {
                    Console.Out.WriteLine(
                        $"  Nearest {divided.TestCount} of {project.SelectedTests} first - {split!.Explanation}.");
                    Console.Out.WriteLine();
                }

                foreach (var filter in waves)
                {
                    var waveExit = Invoke(TestCommandBuilder.Build(project, mode, passthrough, filter));

                    // The first failure, not the last. docs/usage.md has always documented "the
                    // exit code of the first failing dotnet test" while the loop overwrote it with
                    // each later failure - so a run whose first project failed with a distinctive
                    // code reported whatever the last one happened to return.
                    if (waveExit != 0 && exitCode == 0)
                    {
                        exitCode = waveExit;
                    }

                    if (waveExit != 0 && stopEarly)
                    {
                        abandoned = true;
                        break;
                    }
                }

                if (abandoned)
                {
                    break;
                }
            }

            if (abandoned)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("  Stopped at the first failure (--fail-fast); the rest of the selection did not run.");
                Console.Out.WriteLine();
            }

            // The suite is the one term of the break-even the tool spawns and never measured, so
            // `Worth it if the full suite takes more than 14s` could be printed by a tool that had
            // just watched that suite take two. Recorded here and nowhere else: `analyze` runs no
            // tests, so it has nothing to observe.
            //
            // Not recorded when `--fail-fast` cut the run short: the suite time would be however
            // far it got, and a ledger that learned "the suite takes 3s" from a run that stopped
            // after one project would understate T for every later decision that reads it.
            if (ran && !abandoned && options.UseCache)
            {
                RunLedger.Append(cacheDirectory, new RunRecord(
                    At: DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    HeadCommit: report.HeadCommit ?? string.Empty,
                    FullRun: report.IsFullRun,
                    AnalysisSeconds: report.ElapsedSeconds,
                    SuiteSeconds: System.Diagnostics.Stopwatch.GetElapsedTime(suiteStarted).TotalSeconds,
                    SelectedTests: report.SelectedTests,
                    TotalTests: report.TotalTests));

                if (RunLedger.Assess(RunLedger.Read(cacheDirectory)) is { } verdict &&
                    RunLedger.Advice(verdict) is { } advice)
                {
                    Console.Out.WriteLine();
                    Console.Out.WriteLine($"  {advice}");
                    Console.Out.WriteLine("  `dotnet tia stats` shows the figures behind this.");
                    Console.Out.WriteLine();
                }
            }

            return exitCode;
        });

        return command;
    }
}
