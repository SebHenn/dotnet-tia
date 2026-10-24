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
        if (options.SolutionPath is not { Length: > 0 } solution ||
            !File.Exists(ServerProtocol.StatePath(options.ResolvedCacheDirectory)))
        {
            return null;
        }

        var response = await SendAsync(options.RepositoryRoot, solution, ServerRequest.Analyze(options), verbose, cancellationToken).ConfigureAwait(false);
        if (response is not { Ok: true, Report: { } report })
        {
            if (response is { Error: { } error })
            {
                verbose?.Invoke(error == "version mismatch"
                    ? $"tia server: it is version {response.Version} and this is {ServerProtocol.ToolVersion}, so analysing here; `dotnet tia server stop` and start it again"
                    : $"tia server: {error}; analysing here instead");
            }

            return null;
        }

        foreach (var line in response.Log)
        {
            verbose?.Invoke(line);
        }

        verbose?.Invoke("analysed by the tia server");
        return new AnalysisOutcome { Report = report, AllTests = response.AllTests };
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
