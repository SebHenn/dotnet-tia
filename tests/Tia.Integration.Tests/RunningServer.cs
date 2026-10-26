using Tia.Cli.Server;

namespace Tia.Integration.Tests;

/// <summary>
/// A <see cref="ServerHost"/> on the fixture that is already serving. Disposing it asks the host
/// to stop and asserts that it exited 0.
/// </summary>
internal sealed class RunningServer : IAsyncDisposable
{
    private readonly FixtureRepository _repository;
    private readonly Task<int> _serving;

    private RunningServer(FixtureRepository repository, Task<int> serving)
    {
        _repository = repository;
        _serving = serving;
    }

    public static async Task<RunningServer> StartAsync(FixtureRepository repository, string cache, CancellationToken cancellationToken)
    {
        var host = new ServerHost(repository.AnalysisRoot, repository.SolutionPath, cache, TimeSpan.FromMinutes(5), TextWriter.Null);
        var server = new RunningServer(repository, Task.Run(() => host.RunAsync(cancellationToken)));

        try
        {
            await WaitUntilServing(repository, cancellationToken);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }

        return server;
    }

    public static async Task WaitUntilServing(FixtureRepository repository, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await ServerClient.SendAsync(repository.AnalysisRoot, repository.SolutionPath,
                ServerRequest.For("status"), null, cancellationToken);
            if (status is { Ok: true })
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ServerClient.SendAsync(_repository.AnalysisRoot, _repository.SolutionPath,
            ServerRequest.For("stop"), null, CancellationToken.None);
        Assert.Equal(0, await _serving);
    }
}
