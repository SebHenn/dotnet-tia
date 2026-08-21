using Tia.Core.Infrastructure;
using Tia.Core.Reporting;
using Tia.Frameworks;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <param name="RecordLedger">
/// Whether to append what this run cost to <c>.tia/runs.jsonl</c>. False under <c>watch</c>: the
/// ledger's job is to answer "what does the whole suite cost on this repository", and a watch loop
/// would fill it with dozens of partial selections taken seconds apart, which is the same distortion
/// <c>--fail-fast</c> is already kept out of it for.
/// </param>
public sealed record SelectiveRunSettings(
    bool DryRun = false,
    bool FailFast = false,
    bool Verbose = false,
    bool RecordLedger = true)
{
    /// <summary>Everything the caller wrote after <c>--</c>, handed to `dotnet test` unchanged.</summary>
    public IReadOnlyList<string> Passthrough { get; init; } = [];
}

/// <summary>
/// Invokes <c>dotnet test</c> for the projects a report selected.
/// </summary>
/// <remarks>
/// Shared by <c>run</c> and <c>watch</c> rather than copied, because everything subtle about
/// executing a selection lives here: the refusal to report success for a full run that names no
/// project, the two-wave split and the ledger arithmetic that decides whether to take it, the
/// first-failure exit code, and which runs are allowed to teach the ledger what a suite costs.
/// A second copy of that would drift, and the drift would be silent.
/// </remarks>
public static class SelectiveRun
{
    public static int Execute(
        AnalysisReport report,
        AnalysisOptions options,
        SelectiveRunSettings settings,
        CancellationToken cancellationToken)
    {
        if (RunCommand.UnrunnableFullRun(report) is { } refusal)
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
        var ran = false;
        var abandoned = false;

        int Invoke(IReadOnlyList<string> arguments)
        {
            Console.Out.WriteLine($"  > {TestCommandBuilder.Describe(arguments)}");
            Console.Out.WriteLine();

            if (settings.DryRun)
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

            if (settings.Verbose && split is { Split: false })
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
                var waveExit = Invoke(TestCommandBuilder.Build(project, mode, settings.Passthrough, filter));

                // The first failure, not the last. docs/usage.md has always documented "the
                // exit code of the first failing dotnet test" while the loop overwrote it with
                // each later failure - so a run whose first project failed with a distinctive
                // code reported whatever the last one happened to return.
                if (waveExit != 0 && exitCode == 0)
                {
                    exitCode = waveExit;
                }

                if (waveExit != 0 && settings.FailFast)
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
        if (ran && !abandoned && options.UseCache && settings.RecordLedger)
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
    }
}
