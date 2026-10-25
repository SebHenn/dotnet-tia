using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Cli.Server;

/// <summary>
/// A process that keeps one solution's workspace loaded and analyses on request (#76).
/// </summary>
/// <remarks>
/// <para>
/// About 4 s of a 5.3 s warm <c>analyze</c> on this repository is MSBuild opening the solution
/// (<c>docs/benchmarks.md</c>). No cache removes it, because it is work done once per process; the
/// only way not to pay it is to keep the process. <c>watch</c> already does, for one terminal. This
/// does it for every <c>analyze</c> and <c>run</c> in the repository.
/// </para>
/// <para>
/// The report has to be the one a fresh process would produce, so the workspace is brought up to
/// date before every analysis, the same way <c>watch</c> does it but without a file watcher to
/// tell it what moved. A project, props, targets or solution file, an assets file from a restore,
/// <c>global.json</c>, or a source file that appeared: any of those changes what MSBuild would
/// evaluate, so the workspace is reopened. Anything else is a document whose text may have
/// changed, which a refresh reads back off disk.
/// </para>
/// </remarks>
public sealed class ServerHost(string repositoryRoot, string solutionPath, string cacheDirectory, TimeSpan idleTimeout, TextWriter log)
{
    /// <summary>Handlers write here from several connections at once.</summary>
    private readonly TextWriter _log = TextWriter.Synchronized(log);

    private int _requests;

    private int _busy;

    private int _loads;

    private int _waiting;

    private BuildInputs? _snapshot;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var pipeName = ServerProtocol.PipeName(repositoryRoot, solutionPath);
        var statePath = ServerProtocol.StatePath(cacheDirectory);
        var started = DateTimeOffset.UtcNow;

        // Several pipe instances are allowed, so nothing at the pipe itself stops a second server
        // taking the same name and answering half the clients. Asked before the load, which is
        // the expensive part.
        if (await ServerClient.SomethingListensAsync(pipeName, cancellationToken).ConfigureAwait(false))
        {
            _log.WriteLine($"{Now()} a server already listens on {pipeName}; not starting another");
            return 1;
        }

        // Closing the terminal that started it must not take it down, and neither must Ctrl+C in a
        // shell it no longer belongs to. Stop it with `tia server stop`.
        using var hangup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => c.Cancel = true);

        _log.WriteLine($"{Now()} loading {solutionPath}");
        using var resident = await ResidentWorkspace.OpenAsync(solutionPath, repositoryRoot, null, null, cancellationToken).ConfigureAwait(false);
        _loads = 1;
        _snapshot = BuildInputs.Take(repositoryRoot);
        _log.WriteLine($"{Now()} loaded; listening on {pipeName}");

        // Stops accepting; what is already connected is still answered. Cancelling the caller's
        // token instead would abandon an analysis a client is waiting on.
        using var accepting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handlers = new List<Task>();

        // One analysis at a time: there is one workspace, and analyses are not reentrant against
        // it. A connection that arrives meanwhile waits here rather than at the pipe (#95).
        using var workspace = new SemaphoreSlim(1, 1);

        WriteState(statePath, pipeName, started);
        try
        {
            while (!accepting.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(accepting.Token);
                idle.CancelAfter(idleTimeout);

                // Always one instance listening, including while an analysis runs. With a single
                // instance a second client found no pipe to connect to for as long as the first
                // was analysed, gave up after two seconds and loaded the solution itself - paying
                // the load the server exists to save, while the server was about to be free.
                var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                try
                {
                    await pipe.WaitForConnectionAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);

                    if (accepting.IsCancellationRequested)
                    {
                        break;
                    }

                    handlers.RemoveAll(h => h.IsCompleted);
                    if (handlers.Count > 0)
                    {
                        // Not idle: someone is waiting on an answer.
                        continue;
                    }

                    _log.WriteLine(FormattableString.Invariant($"{Now()} idle for {idleTimeout.TotalMinutes:0} minutes; stopping"));
                    return 0;
                }

                handlers.RemoveAll(h => h.IsCompleted);
                handlers.Add(Task.Run(() => HandleAsync(pipe, resident, workspace, started, accepting, cancellationToken), CancellationToken.None));
            }

            await Task.WhenAll(handlers).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            TryDelete(statePath);
        }
    }

    private async Task HandleAsync(
        NamedPipeServerStream pipe,
        ResidentWorkspace resident,
        SemaphoreSlim workspace,
        DateTimeOffset started,
        CancellationTokenSource accepting,
        CancellationToken cancellationToken)
    {
        await using var _ = pipe;

        var request = await ReadRequest(pipe, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        if (!string.Equals(request.Version, ServerProtocol.ToolVersion, StringComparison.Ordinal))
        {
            // Someone upgraded. Answering and carrying on left every later command analysing
            // in-process until somebody thought to restart the server, so it stops instead: the
            // client says so once, and the next `server start` is of the version in use (#95).
            await Reply(pipe, new ServerResponse { Ok = false, Version = ServerProtocol.ToolVersion, Error = ServerProtocol.VersionMismatch, Stopping = true }, cancellationToken).ConfigureAwait(false);
            _log.WriteLine($"{Now()} a client of version {request.Version} asked; stopping, since this is {ServerProtocol.ToolVersion}");
            await accepting.CancelAsync().ConfigureAwait(false);
            return;
        }

        switch (request.Command)
        {
            case "stop":
                await Reply(pipe, Status(started), cancellationToken).ConfigureAwait(false);
                _log.WriteLine($"{Now()} stopped on request");
                await accepting.CancelAsync().ConfigureAwait(false);
                return;

            case "status":
                // Never waits for the workspace, so it answers while an analysis runs.
                await Reply(pipe, Status(started), cancellationToken).ConfigureAwait(false);
                return;

            case "analyze" or "explain":
                Interlocked.Increment(ref _waiting);
                await workspace.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Interlocked.Decrement(ref _waiting);
                    Volatile.Write(ref _busy, 1);
                    _requests++;
                    var (response, next) = await AnalyseAsync(resident, request, _snapshot!, cancellationToken).ConfigureAwait(false);
                    _snapshot = next;
                    await Reply(pipe, response, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                    workspace.Release();
                }

                return;

            default:
                await Reply(pipe, new ServerResponse { Ok = false, Version = ServerProtocol.ToolVersion, Error = $"unknown command '{request.Command}'" }, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task<(ServerResponse Response, BuildInputs Snapshot)> AnalyseAsync(
        ResidentWorkspace resident, ServerRequest request, BuildInputs previous, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var current = BuildInputs.Take(repositoryRoot);
        var reopen = current.RequiresReopen(previous, resident.Knows);

        try
        {
            var options = request.ToOptions(repositoryRoot, solutionPath, line => { lock (lines) { lines.Add(line); } });

            var outcome = await new SolutionAnalyzer(options, async (clock, token) =>
            {
                if (reopen)
                {
                    await resident.ReopenAsync(clock, token).ConfigureAwait(false);
                    _loads++;
                    return resident.Current;
                }

                return resident.Refresh(clock, token).Workspace;
            }).AnalyzeAsync(cancellationToken).ConfigureAwait(false);

            _log.WriteLine(FormattableString.Invariant($"{Now()} analysed against {request.BaseRef}{(reopen ? " after reloading the workspace" : string.Empty)} in {outcome.Report.ElapsedSeconds:0.00}s"));

            // An explanation sends back only its answer: the graph and traversal it read stay here,
            // and so does the report, which the client of `explain` does not print.
            return (request.Command == "explain"
                ? new ServerResponse
                {
                    Ok = true,
                    Version = ServerProtocol.ToolVersion,
                    Explain = request.Symbol ? null : Commands.ExplainQuery.Run(outcome, request.Query ?? string.Empty),
                    SymbolExplain = request.Symbol ? Commands.SymbolQuery.Run(outcome, request.Query ?? string.Empty, repositoryRoot, cancellationToken) : null,
                    Log = lines,
                }
                : new ServerResponse
                {
                    Ok = true,
                    Version = ServerProtocol.ToolVersion,
                    Report = outcome.Report,
                    AllTests = outcome.AllTests,
                    Log = lines,
                }, current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The client repeats the analysis in its own process, which reports the failure the
            // way it always has - including the setup errors that exit 2.
            _log.WriteLine($"{Now()} analysis failed: {ex.GetType().Name}: {ex.Message}");
            return (new ServerResponse { Ok = false, Version = ServerProtocol.ToolVersion, Error = ex.Message }, current);
        }
    }

    private ServerResponse Status(DateTimeOffset started) => new()
    {
        Ok = true,
        Version = ServerProtocol.ToolVersion,
        ProcessId = Environment.ProcessId,
        StartedAt = started.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Requests = _requests,
        Loads = _loads,
        Busy = Volatile.Read(ref _busy) == 1,
        Waiting = Volatile.Read(ref _waiting),
    };

    private async Task<ServerRequest?> ReadRequest(Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            return await ServerProtocol.ReadAsync<ServerRequest>(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.WriteLine($"{Now()} unreadable request: {ex.Message}");
            return null;
        }
    }

    private async Task Reply(Stream pipe, ServerResponse response, CancellationToken cancellationToken)
    {
        try
        {
            await ServerProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // The client went away - it timed out, or was cancelled. Its next run asks again.
            _log.WriteLine($"{Now()} the client left before the answer: {ex.Message}");
        }
    }

    private void WriteState(string statePath, string pipeName, DateTimeOffset started)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            processId = Environment.ProcessId,
            pipe = pipeName,
            version = ServerProtocol.ToolVersion,
            repositoryRoot,
            solutionPath,
            startedAt = started.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }, AnalysisReport.JsonOptions));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale state file only costs the next client one failed connection attempt.
        }
    }

    private static string Now() => DateTimeOffset.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The files whose change means MSBuild would evaluate the solution differently, and the source
/// files that exist, at one moment.
/// </summary>
public sealed class BuildInputs
{
    private static readonly string[] SkippedDirectories = [".git", ".tia", "bin", "obj", "node_modules", ".vs", ".idea"];

    private readonly Dictionary<string, (long Length, DateTime Written)> _inputs;

    private BuildInputs(Dictionary<string, (long, DateTime)> inputs, HashSet<string> sources)
    {
        _inputs = inputs;
        Sources = sources;
    }

    public IReadOnlySet<string> Sources { get; }

    public static BuildInputs Take(string repositoryRoot)
    {
        var inputs = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([repositoryRoot]);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        // A restore rewrites this, and it is what resolves every package reference.
                        Record(inputs, Path.Combine(entry, "project.assets.json"));
                    }

                    if (!SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    sources.Add(Path.GetFullPath(entry));
                }
                else if (ResidentWorkspace.NeedsReopen(entry) ||
                         name.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
                {
                    Record(inputs, entry);
                }
            }
        }

        return new BuildInputs(inputs, sources);
    }

    /// <summary>
    /// Whether the workspace has to be opened again rather than refreshed: a build input changed,
    /// appeared or went, or a source file appeared that the workspace does not have.
    /// </summary>
    /// <param name="knows">Whether the workspace already has a document at this path.</param>
    public bool RequiresReopen(BuildInputs previous, Func<string, bool> knows)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(knows);

        if (_inputs.Count != previous._inputs.Count ||
            _inputs.Any(i => !previous._inputs.TryGetValue(i.Key, out var before) || before != i.Value))
        {
            return true;
        }

        return Sources.Any(s => !previous.Sources.Contains(s) && !knows(s));
    }

    private static void Record(Dictionary<string, (long, DateTime)> inputs, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                inputs[Path.GetFullPath(path)] = (info.Length, info.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable now is the same as absent; if it becomes readable, that is a change.
        }
    }
}
