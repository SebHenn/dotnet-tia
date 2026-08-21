using System.CommandLine;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>
/// Holds the workspace open and re-analyses on every edit.
/// </summary>
/// <remarks>
/// <para>
/// Every other command in this tool is a process that opens a solution, answers one question and
/// exits. That is the right shape for CI and the wrong one for a keyboard, because the largest term
/// in the answer is MSBuild evaluating the solution and it is paid again on every keystroke-driven
/// run. Two attempts to cache it away were measured and declined - see <c>docs/benchmarks.md</c> -
/// for the same reason each time: the load is needed on every run that is not a repeat of the last
/// one. It is only needed once per *process*, which is a thing to exploit rather than to cache.
/// </para>
/// <para>
/// The loop is deliberately dumb. A file changes, the resident workspace re-reads what moved, the
/// ordinary analysis runs against the refreshed snapshot, and - with <c>--run</c> - the ordinary
/// selective run executes it. Nothing about selection, filtering, wave splitting or soundness is
/// re-implemented here; if it were, this command would be the one that quietly drifts.
/// </para>
/// </remarks>
public static class WatchCommand
{
    /// <summary>
    /// How long the tree has to stay quiet before a batch of changes is analysed.
    /// </summary>
    /// <remarks>
    /// A save from an editor is rarely one event: many write through a temporary file and rename,
    /// and a formatter-on-save touches the file twice. Analysing the first event would analyse a
    /// half-written tree and then immediately do it again.
    /// </remarks>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    /// <summary>Directories whose churn is output, not source.</summary>
    private static readonly string[] Ignored = ["bin", "obj", ".git", ".vs", ".idea", "node_modules", "TestResults", "artifacts"];

    public static Command Create(CommonOptions common)
    {
        var run = new Option<bool>("--run")
        {
            Description = "Run the impacted tests after each analysis, instead of only listing them.",
        };

        var failFast = new Option<bool>("--fail-fast")
        {
            Description = "With --run, stop as soon as anything fails.",
        };

        var once = new Option<bool>("--once")
        {
            Description = "Analyse once and exit. For checking the command works without holding a terminal.",
        };

        var command = new Command("watch", "Keep the workspace loaded and re-analyse on every edit.")
        {
            TreatUnmatchedTokensAsErrors = false,
        };

        // No `--json`: this writes a stream of reports interleaved with a test runner's output, and
        // there is no single document for a stream to be. `analyze --json` is the machine-readable
        // form of one analysis.
        common.AddTo(command, common.Json);
        command.Options.Add(run);
        command.Options.Add(failFast);
        command.Options.Add(once);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(common.Verbose);
            var options = common.Read(parseResult, verbose ? Console.Error.WriteLine : null);
            var settings = new SelectiveRunSettings(
                FailFast: parseResult.GetValue(failFast),
                Verbose: verbose,

                // A watch loop would fill the ledger with dozens of partial selections taken
                // seconds apart, and the ledger's whole job is to know what the *suite* costs.
                RecordLedger: false)
            {
                Passthrough = parseResult.UnmatchedTokens.ToList(),
            };

            var solutionPath = options.SolutionPath ?? WorkspaceLoader.FindSolutionOrProject(options.RepositoryRoot);
            if (solutionPath is null)
            {
                Console.Error.WriteLine($"  No solution or project found in '{options.RepositoryRoot}'. Pass --solution.");
                return 1;
            }

            options = options with { SolutionPath = solutionPath };

            Console.Out.WriteLine($"  Loading {Path.GetFileName(solutionPath)}...");

            // Timed and printed, because it is the whole argument for this command existing and a
            // reader should be able to check it rather than take it. Every later analysis is what
            // it costs *not* to pay this again.
            var loadStarted = System.Diagnostics.Stopwatch.GetTimestamp();

            using var resident = await ResidentWorkspace
                .OpenAsync(solutionPath, options.RepositoryRoot, options.Log, null, cancellationToken)
                .ConfigureAwait(false);

            var loadSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(loadStarted).TotalSeconds;

            using var changes = new ChangeSignal(options.RepositoryRoot);

            var reopen = false;
            var refreshed = 0;

            // The refresh happens inside the analysis rather than before it so that the time it
            // takes is charged to that analysis's clock and shows up in its own report.
            var outcome = await AnalyseAsync().ConfigureAwait(false);
            Report(outcome, refreshed, first: true);

            if (parseResult.GetValue(run))
            {
                SelectiveRun.Execute(outcome.Report, options, settings, cancellationToken);
            }

            if (parseResult.GetValue(once))
            {
                return 0;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                Console.Out.WriteLine("  Watching for changes. Ctrl+C to stop.");
                Console.Out.WriteLine();

                var batch = await changes.NextAsync(Quiet, cancellationToken).ConfigureAwait(false);
                if (batch is null)
                {
                    return 0;
                }

                // A project file moves MSBuild's own item globs, and a file that appears belongs to
                // whichever globs claim it. Neither is a question a refreshed snapshot can answer,
                // so both pay the load again rather than guess.
                reopen = batch.Lost
                         || batch.Paths.Any(ResidentWorkspace.NeedsReopen)
                         || batch.Paths.Any(p => IsSource(p) && File.Exists(p) && !resident.Knows(p));

                if (reopen)
                {
                    Console.Out.WriteLine(batch.Lost
                        ? "  The watcher dropped events, so the workspace is being reloaded."
                        : "  A project file or a new source file changed, so the workspace is being reloaded.");
                }

                outcome = await AnalyseAsync().ConfigureAwait(false);
                Report(outcome, refreshed, first: false);

                if (parseResult.GetValue(run))
                {
                    SelectiveRun.Execute(outcome.Report, options, settings, cancellationToken);
                }
            }

            return 0;

            async Task<AnalysisOutcome> AnalyseAsync() =>
                await new SolutionAnalyzer(options, async (clock, token) =>
                {
                    if (reopen)
                    {
                        await resident.ReopenAsync(clock, token).ConfigureAwait(false);
                        reopen = false;
                        refreshed = 0;
                        return resident.Current;
                    }

                    var result = resident.Refresh(clock, token);
                    refreshed = result.ChangedFiles;
                    return result.Workspace;
                }).AnalyzeAsync(cancellationToken).ConfigureAwait(false);

            void Report(AnalysisOutcome analysed, int documents, bool first)
            {
                var report = analysed.Report;
                Console.Out.WriteLine();

                Console.Out.WriteLine(first
                    ? FormattableString.Invariant(
                        $"  Loaded in {loadSeconds:0.00}s, analysed in {report.ElapsedSeconds:0.00}s. ") +
                      "The load is paid once; what follows is what each edit costs."
                    : FormattableString.Invariant(
                        $"  {documents} file(s) changed - analysed in {report.ElapsedSeconds:0.00}s"));

                if (verbose)
                {
                    var timings = report.Timings;
                    Console.Out.WriteLine(FormattableString.Invariant(
                        $"  re-read {timings.RefreshSeconds:0.00}s / graph {timings.GraphSeconds:0.00}s / ") +
                      FormattableString.Invariant(
                        $"diff {timings.DiffSeconds:0.00}s / resolve {timings.ChangeResolutionSeconds:0.00}s"));
                }

                Console.Out.Write(ReportRenderer.Render(report, verbose));
            }
        });

        return command;
    }

    private static bool IsSource(string path) =>
        Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <param name="Lost">
    /// The watcher overflowed its buffer and cannot say what changed. Everything after that is
    /// unknown, so the caller reloads rather than analysing a snapshot it has no reason to trust.
    /// </param>
    private sealed record ChangeBatch(IReadOnlyList<string> Paths, bool Lost);

    /// <summary>
    /// A file watcher over the repository, collapsed into one "something changed" signal.
    /// </summary>
    private sealed class ChangeSignal : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _root;
        private bool _lost;

        public ChangeSignal(string repositoryRoot)
        {
            _root = repositoryRoot;
            _watcher = new FileSystemWatcher(repositoryRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,

                // The default is 8 KB, which a build's output churn overruns easily - and an
                // overrun is reported as an error, not as the events it swallowed.
                InternalBufferSize = 64 * 1024,
            };

            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Waits for a change and then for the tree to go quiet, and returns what moved. Null when
        /// the wait was cancelled.
        /// </summary>
        public async Task<ChangeBatch?> NextAsync(TimeSpan quiet, CancellationToken cancellationToken)
        {
            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Drain: keep extending the window until nothing new has arrived for `quiet`.
                while (await _signal.WaitAsync(quiet, cancellationToken).ConfigureAwait(false))
                {
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            lock (_paths)
            {
                var batch = new ChangeBatch([.. _paths], _lost);
                _paths.Clear();
                _lost = false;
                return batch;
            }
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _signal.Dispose();
        }

        private void OnChanged(object sender, FileSystemEventArgs e) => Record(e.FullPath);

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Record(e.OldFullPath);
            Record(e.FullPath);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            lock (_paths)
            {
                _lost = true;
            }

            Release();
        }

        private void Record(string path)
        {
            if (Interesting(path))
            {
                lock (_paths)
                {
                    _paths.Add(path);
                }

                Release();
            }
        }

        /// <remarks>
        /// The tool's own cache directory is excluded along with the build output: a run writes
        /// <c>.tia/graph-*.bin</c>, which would wake the loop that wrote it.
        /// </remarks>
        private bool Interesting(string path)
        {
            if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = path[_root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return !segments.Any(segment =>
                segment.Equals(".tia", StringComparison.OrdinalIgnoreCase) ||
                Ignored.Contains(segment, StringComparer.OrdinalIgnoreCase));
        }

        private void Release()
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled and not yet consumed, which is the whole point of a count of
                // one: the batch it wakes is read from `_paths`, not from the semaphore.
            }
        }
    }
}
