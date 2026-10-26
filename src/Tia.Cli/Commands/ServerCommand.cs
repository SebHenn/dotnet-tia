using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Tia.Cli.Server;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>
/// <c>tia server start|stop|status|run</c>: keep the workspace loaded between commands (#76).
/// </summary>
/// <remarks>
/// <c>start</c> launches <c>run</c> as a background process and waits for it to accept. Once one is
/// running for a repository and solution, <c>analyze</c> and <c>run</c> there send it their analysis
/// instead of loading the solution themselves; <c>--no-server</c> on either skips it.
/// </remarks>
public static class ServerCommand
{
    private static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(30);

    /// <summary>The first load can take as long as MSBuild does; this is far past it.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    public static Command Create(CommonOptions common)
    {
        var idle = new Option<int?>("--idle-minutes")
        {
            Description = "Stop after this many minutes without a request. Defaults to 30.",
            CustomParser = result => CommonOptions.ParseBounded<int>(
                result,
                "--idle-minutes",
                token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null,
                value => value >= 1,
                "a whole number of minutes, 1 or more"),
        };

        var server = new Command("server", "Keep the workspace loaded in a background process, so analyze and run skip loading it.");

        var start = new Command("start", "Start a server for this repository and solution, and wait until it accepts.");
        start.Options.Add(common.Path);
        start.Options.Add(common.Solution);
        start.Options.Add(common.CacheDirectory);
        start.Options.Add(idle);
        start.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var (root, solution, cache) = Resolve(common, parseResult);

            var answer = await Ask(root, solution, "status", cancellationToken).ConfigureAwait(false);
            if (answer is { Ok: true } running)
            {
                Console.Out.WriteLine($"  A server is already running here (pid {running.ProcessId}, version {running.Version}).");
                return ExitCodes.Ok;
            }

            if (answer is { Error: ServerProtocol.VersionMismatch })
            {
                Console.Out.WriteLine($"  {Capitalise(ServerProtocol.DescribeMismatch(answer))}");
                if (!answer.Stopping || !await Released(root, solution, cancellationToken).ConfigureAwait(false))
                {
                    return ExitCodes.Failed;
                }
            }

            var minutes = parseResult.GetValue(idle) ?? (int)DefaultIdle.TotalMinutes;
            Launch(root, solution, cache, minutes);

            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < StartTimeout)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (await Status(root, solution, cancellationToken).ConfigureAwait(false) is { } up)
                {
                    Console.Out.WriteLine(FormattableString.Invariant(
                        $"  Server ready in {waited.Elapsed.TotalSeconds:0.0}s (pid {up.ProcessId}). It stops after {minutes} idle minutes, or with `dotnet tia server stop`."));
                    Console.Out.WriteLine($"  Log: {Path.Combine(cache, "server.log")}");
                    return ExitCodes.Ok;
                }
            }

            Console.Error.WriteLine($"  The server did not start within {StartTimeout.TotalMinutes:0} minutes. See {Path.Combine(cache, "server.log")}.");
            return ExitCodes.Inconclusive;
        }));

        var stop = new Command("stop", "Stop the server for this repository and solution.");
        stop.Options.Add(common.Path);
        stop.Options.Add(common.Solution);
        stop.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var (root, solution, _) = Resolve(common, parseResult);
            var stopped = await Ask(root, solution, "stop", cancellationToken).ConfigureAwait(false);
            Console.Out.WriteLine(stopped switch
            {
                { Ok: true } => $"  Stopped the server (pid {stopped.ProcessId}) after {stopped.Requests} analyses.",
                { Error: ServerProtocol.VersionMismatch, Stopping: true } => $"  Stopped the server of version {stopped.Version}.",
                { Error: ServerProtocol.VersionMismatch } => $"  {Capitalise(ServerProtocol.DescribeMismatch(stopped))}",
                _ => "  No server is running here.",
            });
            return stopped is { Error: ServerProtocol.VersionMismatch, Stopping: false } ? ExitCodes.Failed : ExitCodes.Ok;
        }));

        var status = new Command("status", "Say whether a server is running for this repository and solution.");
        status.Options.Add(common.Path);
        status.Options.Add(common.Solution);
        status.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var (root, solution, _) = Resolve(common, parseResult);
            var answer = await Ask(root, solution, "status", cancellationToken).ConfigureAwait(false);
            if (answer is { Error: ServerProtocol.VersionMismatch })
            {
                Console.Out.WriteLine($"  {Capitalise(ServerProtocol.DescribeMismatch(answer))}");
                return ExitCodes.Failed;
            }

            if (answer is not { Ok: true } running)
            {
                Console.Out.WriteLine("  No server is running here.");
                return ExitCodes.Failed;
            }

            Console.Out.WriteLine($"  Running: pid {running.ProcessId}, version {running.Version}, since {running.StartedAt}.");
            Console.Out.WriteLine($"  {running.Requests} analyses served, workspace loaded {running.Loads} time(s).");
            if (running.Busy)
            {
                Console.Out.WriteLine($"  Analysing now, with {running.Waiting} more waiting.");
            }

            return ExitCodes.Ok;
        }));

        // What `start` launches. Run by hand, it serves in the foreground until Ctrl+C.
        var run = new Command("run", "Serve in the foreground. `start` runs this in the background.");
        run.Options.Add(common.Path);
        run.Options.Add(common.Solution);
        run.Options.Add(common.CacheDirectory);
        run.Options.Add(idle);
        run.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var (root, solution, cache) = Resolve(common, parseResult);
            var minutes = parseResult.GetValue(idle) ?? (int)DefaultIdle.TotalMinutes;

            Directory.CreateDirectory(cache);
            await using var log = new StreamWriter(Path.Combine(cache, "server.log"), append: true) { AutoFlush = true };
            var host = new ServerHost(root, solution, cache, TimeSpan.FromMinutes(minutes), log);
            return await host.RunAsync(cancellationToken).ConfigureAwait(false);
        }));

        server.Subcommands.Add(start);
        server.Subcommands.Add(stop);
        server.Subcommands.Add(status);
        server.Subcommands.Add(run);
        return server;
    }

    private static (string Root, string Solution, string Cache) Resolve(CommonOptions common, ParseResult parseResult)
    {
        var options = common.Read(parseResult, null);
        SetupException.RequireRepository(options.RepositoryRoot);
        var solution = SetupException.RequireSolution(options.RepositoryRoot, options.SolutionPath);
        return (options.RepositoryRoot, solution, options.ResolvedCacheDirectory);
    }

    private static async Task<ServerResponse?> Status(string root, string solution, CancellationToken cancellationToken) =>
        await Ask(root, solution, "status", cancellationToken).ConfigureAwait(false) is { Ok: true } status
            ? status
            : null;

    private static Task<ServerResponse?> Ask(string root, string solution, string command, CancellationToken cancellationToken) =>
        ServerClient.SendAsync(root, solution, ServerRequest.For(command), null, cancellationToken);

    /// <summary>Waits for a server that said it is stopping to let go of its pipe, so a new one can take it.</summary>
    private static async Task<bool> Released(string root, string solution, CancellationToken cancellationToken)
    {
        var pipeName = ServerProtocol.PipeName(root, solution);
        var waited = Stopwatch.StartNew();
        while (await ServerClient.SomethingListensAsync(pipeName, cancellationToken).ConfigureAwait(false))
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(30))
            {
                Console.Error.WriteLine("  The old server did not stop within 30 seconds.");
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// This same tool, as a process that outlives this one. Through <c>dotnet</c> when that is how
    /// it is running now, as a local tool or from a build, and directly when it is the apphost.
    /// </summary>
    private static void Launch(string root, string solution, string cache, int idleMinutes)
    {
        var host = Environment.ProcessPath ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Its own streams, closed at once below. Inheriting this process's would keep a pipe
            // like `dotnet tia server start | tee` open for as long as the server lives. It writes
            // to server.log, not to these.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(ServerCommand).Assembly.Location);
        }

        foreach (var argument in new[]
                 {
                     "server", "run", "--path", root, "--solution", solution, "--cache-dir", cache,
                     "--idle-minutes", idleMinutes.ToString(CultureInfo.InvariantCulture),
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Not awaited and not killed: the point is that it keeps running.
        using var process = Process.Start(startInfo);
        if (process is not null)
        {
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
    }
}
