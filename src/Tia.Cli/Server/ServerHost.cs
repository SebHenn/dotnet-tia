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
    private int _requests;

    private int _loads;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var pipeName = ServerProtocol.PipeName(repositoryRoot, solutionPath);
        var statePath = ServerProtocol.StatePath(cacheDirectory);
        var started = DateTimeOffset.UtcNow;

        // Closing the terminal that started it must not take it down, and neither must Ctrl+C in a
        // shell it no longer belongs to. Stop it with `tia server stop`.
        using var hangup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => c.Cancel = true);

        log.WriteLine($"{Now()} loading {solutionPath}");
        using var resident = await ResidentWorkspace.OpenAsync(solutionPath, repositoryRoot, null, null, cancellationToken).ConfigureAwait(false);
        _loads = 1;
        var snapshot = BuildInputs.Take(repositoryRoot);
        log.WriteLine($"{Now()} loaded; listening on {pipeName}");

        WriteState(statePath, pipeName, started);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(idleTimeout);

                await using var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                try
                {
                    await pipe.WaitForConnectionAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    log.WriteLine(FormattableString.Invariant($"{Now()} idle for {idleTimeout.TotalMinutes:0} minutes; stopping"));
                    return 0;
                }

                var request = await ReadRequest(pipe, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    continue;
                }

                if (!string.Equals(request.Version, ServerProtocol.ToolVersion, StringComparison.Ordinal))
                {
                    // Not an error to the client, which falls back to analysing itself, but the
                    // server is now the wrong version for whoever upgraded.
                    await Reply(pipe, new ServerResponse { Ok = false, Version = ServerProtocol.ToolVersion, Error = "version mismatch" }, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                switch (request.Command)
                {
                    case "stop":
                        await Reply(pipe, Status(started), cancellationToken).ConfigureAwait(false);
                        log.WriteLine($"{Now()} stopped on request");
                        return 0;

                    case "status":
                        await Reply(pipe, Status(started), cancellationToken).ConfigureAwait(false);
                        continue;

                    case "analyze":
                        _requests++;
                        var (response, next) = await AnalyseAsync(resident, request, snapshot, cancellationToken).ConfigureAwait(false);
                        snapshot = next;
                        await Reply(pipe, response, cancellationToken).ConfigureAwait(false);
                        continue;

                    default:
                        await Reply(pipe, new ServerResponse { Ok = false, Version = ServerProtocol.ToolVersion, Error = $"unknown command '{request.Command}'" }, cancellationToken).ConfigureAwait(false);
                        continue;
                }
            }

            return 0;
        }
        finally
        {
            TryDelete(statePath);
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

            log.WriteLine(FormattableString.Invariant($"{Now()} analysed against {request.BaseRef}{(reopen ? " after reloading the workspace" : string.Empty)} in {outcome.Report.ElapsedSeconds:0.00}s"));

            return (new ServerResponse
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
            log.WriteLine($"{Now()} analysis failed: {ex.GetType().Name}: {ex.Message}");
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
    };

    private async Task<ServerRequest?> ReadRequest(Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            return await ServerProtocol.ReadAsync<ServerRequest>(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.WriteLine($"{Now()} unreadable request: {ex.Message}");
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
            log.WriteLine($"{Now()} the client left before the answer: {ex.Message}");
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
