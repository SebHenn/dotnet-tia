using System.IO.Pipes;
using System.Text.Json;
using Tia.Workspace;

namespace Tia.Cli.Server;

/// <summary>
/// Asks a running <c>tia server</c> for an analysis, or says there is none to ask (#76).
/// </summary>
/// <remarks>
/// Every failure here is the same answer: analyse in this process, as if no server existed. A
/// server that is not running, of another version, mid-reload, or broken costs a fallback and a
/// line under <c>--verbose</c> - never a different report, and never a failed command.
/// </remarks>
public static class ServerClient
{
    /// <summary>How long to wait for a server that has said it is running to accept.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public static async Task<AnalysisOutcome?> TryAnalyzeAsync(
        AnalysisOptions options,
        Action<string>? verbose,
        CancellationToken cancellationToken)
    {
        var response = await AskAsync(options, ServerRequest.Analyze(options), r => r.Report is not null, verbose, cancellationToken).ConfigureAwait(false);
        if (response is not { Report: { } report })
        {
            return null;
        }

        foreach (var line in response.Log)
        {
            verbose?.Invoke(line);
        }

        verbose?.Invoke("analysed by the tia server");
        return new AnalysisOutcome { Report = report, AllTests = response.AllTests, Served = true };
    }

    /// <summary>
    /// <c>explain</c>, answered by the server (#96): the query runs next to its workspace, and only
    /// the answer comes back. Falls back exactly as an analysis does.
    /// </summary>
    public static async Task<Commands.ExplainResult?> TryExplainAsync(
        AnalysisOptions options,
        string query,
        Action<string>? verbose,
        CancellationToken cancellationToken)
    {
        var request = ServerRequest.Analyze(options) with { Command = "explain", Query = query };
        var response = await AskAsync(options, request, r => r.Explain is not null, verbose, cancellationToken).ConfigureAwait(false);
        if (response is not { Explain: { } explained })
        {
            return null;
        }

        foreach (var line in response.Log)
        {
            verbose?.Invoke(line);
        }

        verbose?.Invoke("explained by the tia server");
        return explained;
    }

    /// <summary><c>explain --symbol</c>, answered by the server (#98).</summary>
    public static async Task<Commands.SymbolResult?> TryExplainSymbolAsync(
        AnalysisOptions options,
        string query,
        Action<string>? verbose,
        CancellationToken cancellationToken)
    {
        var request = ServerRequest.Analyze(options) with { Command = "explain", Query = query, Symbol = true };
        var response = await AskAsync(options, request, r => r.SymbolExplain is not null, verbose, cancellationToken).ConfigureAwait(false);
        if (response is not { SymbolExplain: { } found })
        {
            return null;
        }

        verbose?.Invoke("explained by the tia server");
        return found;
    }

    /// <summary>
    /// The server's response when there is a server for this solution and it answered, and null
    /// otherwise.
    /// </summary>
    private static async Task<ServerResponse?> AskAsync(
        AnalysisOptions options,
        ServerRequest request,
        Func<ServerResponse, bool> answered,
        Action<string>? verbose,
        CancellationToken cancellationToken)
    {
        if (options.SolutionPath is not { Length: > 0 } solution ||
            !File.Exists(ServerProtocol.StatePath(options.ResolvedCacheDirectory)))
        {
            return null;
        }

        var response = await SendAsync(options.RepositoryRoot, solution, request, verbose, cancellationToken).ConfigureAwait(false);
        if (response is not { Ok: true } || !answered(response))
        {
            Unanswered(response, verbose);
            return null;
        }

        return response;
    }

    private static void Unanswered(ServerResponse? response, Action<string>? verbose)
    {
        if (response is { Error: ServerProtocol.VersionMismatch })
        {
            // Not only under --verbose: the server was meant to save this load, and the reader
            // should know why it did not and what to run. A server that stops on the mismatch
            // leaves no state file behind, so this is said once (#95).
            Console.Error.WriteLine($"  Analysing in this process: {ServerProtocol.DescribeMismatch(response)}");
        }
        else if (response is { Error: { } error })
        {
            verbose?.Invoke($"tia server: {error}; analysing here instead");
        }
    }

    /// <summary>Whether anything accepts a connection on this pipe within a moment.</summary>
    public static async Task<bool> SomethingListensAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromMilliseconds(500));
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task<ServerResponse?> SendAsync(
        string repositoryRoot,
        string solutionPath,
        ServerRequest request,
        Action<string>? verbose,
        CancellationToken cancellationToken)
    {
        var pipeName = ServerProtocol.PipeName(repositoryRoot, solutionPath);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }

            await ServerProtocol.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            return await ServerProtocol.ReadAsync<ServerResponse>(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            verbose?.Invoke("tia server: no answer within 2s, so analysing here");
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException)
        {
            verbose?.Invoke($"tia server: {ex.Message}; analysing here instead");
            return null;
        }
    }
}
