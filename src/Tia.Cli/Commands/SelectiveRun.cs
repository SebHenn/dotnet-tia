using Tia.Core.Infrastructure;
using Tia.Core.Reporting;
using Tia.Core.Validation;
using Tia.Frameworks;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <param name="RecordLedger">
/// Whether to append what this run cost to <c>.tia/runs.jsonl</c>. False under <c>watch</c>: the
/// ledger's job is to answer "what does the whole suite cost on this repository", and a watch loop
/// would fill it with dozens of partial selections taken seconds apart, which is the same distortion
/// <c>--fail-fast</c> is already kept out of it for.
/// </param>
/// <param name="AnalysisServed">
/// Whether <c>tia server</c> did the analysis, which the ledger records and judges the run by: a
/// served analysis costs a fraction of one done here (#97).
/// </param>
/// <param name="MaxParallel">
/// How many projects may run at once (#74). 1 runs them in turn, streaming output as it comes.
/// </param>
/// <param name="RecordFailures">
/// Ask each runner that can for TRX, and record which tests failed in <c>.tia/last-failed.json</c>
/// for the next <c>run --failed-first</c> (#72).
/// </param>
public sealed record SelectiveRunSettings(
    bool DryRun = false,
    bool FailFast = false,
    bool Verbose = false,
    bool RecordLedger = true,
    bool RecordFailures = false,
    int MaxParallel = 1,
    bool AnalysisServed = false)
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

            // Distinguished from an ordinary test failure on purpose: nothing ran, so 1 would
            // claim a result this command never obtained. It is the same answer `analyze` and
            // `shadow` give when they cannot conclude anything.
            return report.WorkspaceUnusable ? ExitCodes.Inconclusive : ExitCodes.Failed;
        }

        // One invocation per multi-targeted project wherever its frameworks selected the same
        // tests, rather than one per framework that each ran all of them (#67).
        var projects = TestCommandBuilder.Coalesce([.. report.Projects.Where(p => p.SelectedTests > 0)]).ToList();

        // Run whole, after the analysed projects: an F# or VB test project has no tests to
        // select, so one that could be affected runs unfiltered (#44).
        projects.AddRange(report.UnanalysedTestProjects.Where(p => p.Runs).Select(p => new ProjectSelection
        {
            Name = p.Name,
            ProjectPath = p.ProjectPath,
            Framework = "Unknown",
            Runner = "VsTest",
            TotalTests = 0,
            SelectedTests = 0,
            Filtered = false,
            UnfilteredReason = $"{p.Language}, not analysed",
        }));

        if (projects.Count == 0)
        {
            Console.Out.WriteLine(report.Shard is { } shard
                ? $"  Nothing to run in shard {shard.Index}/{shard.Count} - the other shards have the whole selection."
                : "  Nothing to run - no test was impacted by this diff.");
            Console.Out.WriteLine();
            return 0;
        }

        var cacheDirectory = options.ResolvedCacheDirectory;

        // Read once, before anything runs: what the suite has cost on this repository is what
        // decides whether a second invocation per project is worth its own start-up.
        var ledger = options.UseCache ? RunLedger.Assess(RunLedger.Read(cacheDirectory), settings.AnalysisServed) : null;

        var exitCode = 0;
        var mode = TestCommandBuilder.ModeOf(report);
        var suiteStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var ran = false;
        var abandoned = false;

        // Outside the repository: nothing a run writes for its own bookkeeping belongs in the tree
        // the next diff is taken of.
        var trxDirectory = settings.RecordFailures && !settings.DryRun
            ? Directory.CreateTempSubdirectory("tia-results-").FullName
            : null;

        // Side by side only against binaries that are already built (#74): two `dotnet test`
        // invocations that each build would build the projects they share at the same time.
        var parallel = settings.MaxParallel > 1 && !settings.DryRun && projects.Count > 1;
        if (parallel && !BuildsNothing(settings.Passthrough))
        {
            Console.Out.WriteLine("  --parallel: running one project at a time, because these invocations would each build. " +
                                  "Let `run` build ahead (pass nothing after --), or pass --no-build after --.");
            Console.Out.WriteLine();
            parallel = false;
        }

        ProjectRun RunProject(ProjectSelection project, int index, RunOutput output, CancellationToken token)
        {
            var projectRan = false;
            var projectExit = 0;

            // Asked for, so taken without consulting the ledger (#72).
            var lastFailed = project.FirstWave is { LastFailed: true } failedWave ? failedWave : null;

            var split = lastFailed is null && project.FirstWave is { } candidate
                ? RunLedger.AssessSplit(ledger, candidate.TestCount, project.SelectedTests, report.TotalTests)
                : null;

            if (settings.Verbose && split is { Split: false })
            {
                output.Line($"  {project.Name} runs in one invocation: {split.Explanation}");
            }

            // Two invocations, nearest tests first. The remainder still runs by default: the
            // wave buys the developer the failure in seconds, and stopping there would take
            // away the complete list that a pull request needs. `--fail-fast` is for when the
            // first answer is the only one wanted.
            var divided = lastFailed ?? (split is { Split: true } ? project.FirstWave : null);

            List<IReadOnlyList<string>> waves = divided is null
                ? [project.FilterArguments]
                : [divided.FilterArguments, divided.RemainderFilterArguments];

            if (lastFailed is not null)
            {
                output.Line(project.Filtered
                    ? $"  {lastFailed.TestCount} of {project.SelectedTests} first - the classes of tests that failed last time."
                    : $"  {lastFailed.TestCount} first - the classes of tests that failed last time; they run again with the rest of the project.");
                output.Line(string.Empty);
            }
            else if (divided is not null)
            {
                output.Line($"  Nearest {divided.TestCount} of {project.SelectedTests} first - {split!.Explanation}.");
                output.Line(string.Empty);
            }

            // One directory per project, so that two runners writing at once never pick the same
            // report name.
            var projectTrx = trxDirectory is null
                ? null
                : Path.Combine(trxDirectory, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

            for (var w = 0; w < waves.Count; w++)
            {
                // A wave of a project that otherwise runs whole still has a filter to pass.
                var invoked = w == 0 && divided is not null && !project.Filtered ? project with { Filtered = true } : project;
                var arguments = TestCommandBuilder.Build(invoked, mode, settings.Passthrough, waves[w], projectTrx);

                output.Line($"  > {TestCommandBuilder.Describe(arguments)}");
                output.Line(string.Empty);

                var waveExit = 0;
                if (!settings.DryRun)
                {
                    projectRan = true;
                    waveExit = ProcessRunner.RunStreaming("dotnet", arguments, options.RepositoryRoot, output.Line, output.Error, token);
                }

                // The first failure, not the last. docs/usage.md has always documented "the
                // exit code of the first failing dotnet test" while the loop overwrote it with
                // each later failure - so a run whose first project failed with a distinctive
                // code reported whatever the last one happened to return.
                if (waveExit != 0 && projectExit == 0)
                {
                    projectExit = waveExit;
                }

                if (waveExit != 0 && settings.FailFast)
                {
                    return new ProjectRun(projectExit, projectRan, Abandoned: true);
                }
            }

            return new ProjectRun(projectExit, projectRan, Abandoned: false);
        }

        var results = parallel
            ? RunSideBySide(projects, settings, RunProject, cancellationToken)
            : RunInTurn(projects, RunProject, cancellationToken);

        ran = results.Any(r => r.Ran);
        abandoned = results.Any(r => r.Abandoned);

        // In project order rather than finishing order, so the code reported does not depend on
        // which project happened to be quicker.
        exitCode = results.Select(r => r.Exit).FirstOrDefault(e => e != 0);

        if (abandoned)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(parallel
                ? "  Stopped at the first failure (--fail-fast); projects still running were stopped and the rest did not start."
                : "  Stopped at the first failure (--fail-fast); the rest of the selection did not run.");
            Console.Out.WriteLine();
        }

        if (trxDirectory is not null)
        {
            RecordFailures(trxDirectory, cacheDirectory, options.LastFailed, projects, settings.Verbose);
        }

        // The suite is the one term of the break-even the tool spawns and never measured, so
        // `Worth it if the full suite takes more than 14s` could be printed by a tool that had
        // just watched that suite take two. Recorded here and nowhere else: `analyze` runs no
        // tests, so it has nothing to observe.
        //
        // Not recorded when `--fail-fast` cut the run short: the suite time would be however
        // far it got, and a ledger that learned "the suite takes 3s" from a run that stopped
        // after one project would understate T for every later decision that reads it.
        // Nor from a parallel run, whose wall-clock is not the suite time the break-even compares
        // a selection against.
        if (ran && !abandoned && !parallel && options.UseCache && settings.RecordLedger)
        {
            RunLedger.Append(cacheDirectory, new RunRecord(
                At: DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                HeadCommit: report.HeadCommit ?? string.Empty,
                FullRun: report.IsFullRun,
                AnalysisSeconds: report.ElapsedSeconds,
                SuiteSeconds: System.Diagnostics.Stopwatch.GetElapsedTime(suiteStarted).TotalSeconds,
                SelectedTests: report.SelectedTests,
                TotalTests: report.TotalTests,
                Served: settings.AnalysisServed));

            if (RunLedger.Assess(RunLedger.Read(cacheDirectory), settings.AnalysisServed) is { } verdict &&
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

    public sealed record ProjectRun(int Exit, bool Ran, bool Abandoned);

    public delegate ProjectRun ProjectRunner(ProjectSelection project, int index, RunOutput output, CancellationToken cancellationToken);

    public static List<ProjectRun> RunInTurn(
        IReadOnlyList<ProjectSelection> projects,
        ProjectRunner run,
        CancellationToken cancellationToken)
    {
        var console = RunOutput.Console();
        var results = new List<ProjectRun>();

        for (var i = 0; i < projects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = run(projects[i], i, console, cancellationToken);
            results.Add(result);

            if (result.Abandoned)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Up to <see cref="SelectiveRunSettings.MaxParallel"/> projects at once (#74). Each project's
    /// output is held and printed whole when it finishes, because interleaved lines from several
    /// runners cannot be read; the order between projects is the order they finish in.
    /// </summary>
    public static List<ProjectRun> RunSideBySide(
        IReadOnlyList<ProjectSelection> projects,
        SelectiveRunSettings settings,
        ProjectRunner run,
        CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(settings.MaxParallel);
        var printing = new object();

        var tasks = projects.Select((project, index) => Task.Run(() =>
        {
            try
            {
                slots.Wait(stop.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // --fail-fast stopped the run before this project started.
                return new ProjectRun(0, Ran: false, Abandoned: true);
            }

            var buffer = RunOutput.Buffered();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var result = run(project, index, buffer, stop.Token);
                if (result.Abandoned)
                {
                    stop.Cancel();
                }

                lock (printing)
                {
                    var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
                    Console.Out.WriteLine(FormattableString.Invariant($"  -- {project.Name}: exit {result.Exit}, {seconds:0.0}s"));
                    buffer.WriteTo(Console.Out, Console.Error);
                }

                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lock (printing)
                {
                    Console.Out.WriteLine($"  -- {project.Name}: stopped (--fail-fast)");
                }

                return new ProjectRun(0, Ran: true, Abandoned: true);
            }
            finally
            {
                slots.Release();
            }
        })).ToArray();

        return [.. Task.WhenAll(tasks).GetAwaiter().GetResult()];
    }

    /// <summary>Whether the caller's arguments stop <c>dotnet test</c> building: <c>--no-build</c> before any <c>--</c>.</summary>
    public static bool BuildsNothing(IReadOnlyList<string> passthrough) =>
        passthrough.TakeWhile(a => a != "--").Contains("--no-build", StringComparer.Ordinal);

    /// <summary>
    /// Folds this run's TRX into the record of last failures, and removes the reports. What ran
    /// takes this run's outcome; what did not, keeps the one it had.
    /// </summary>
    private static void RecordFailures(
        string trxDirectory,
        string cacheDirectory,
        IReadOnlySet<string> previous,
        IReadOnlyList<ProjectSelection> projects,
        bool verbose)
    {
        try
        {
            var outcomes = Directory.EnumerateFiles(trxDirectory, "*.trx", SearchOption.AllDirectories)
                .SelectMany(ReadOrEmpty)
                .ToList();

            if (verbose)
            {
                foreach (var project in projects.Where(p => !p.WritesTrx))
                {
                    Console.Out.WriteLine($"  {project.Name} cannot write TRX, so --failed-first does not record its failures.");
                }
            }

            if (outcomes.Count == 0)
            {
                return;
            }

            var failed = outcomes.Where(o => o.Result.Equals("Failed", StringComparison.OrdinalIgnoreCase)).Select(o => o.Name).ToList();
            LastFailed.Write(cacheDirectory, LastFailed.Merge(previous, outcomes.Select(o => o.Name), failed));

            if (failed.Count > 0)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine($"  {failed.Distinct(StringComparer.Ordinal).Count()} failing test(s) recorded; `run --failed-first` runs them first next time.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(trxDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a run over.
            }
        }
    }

    /// <summary>Names as the selection writes them: a theory's arguments are not part of it.</summary>
    private static IEnumerable<TestOutcome> ReadOrEmpty(string trx)
    {
        try
        {
            return [.. TrxParser.Parse(trx).Select(o => o with { Name = TrxParser.NormalizeTestName(o.Name) })];
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }
    }
}
