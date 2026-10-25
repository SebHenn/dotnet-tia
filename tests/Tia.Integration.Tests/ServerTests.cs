using System.Text.Json;
using Tia.Cli.Server;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// <c>tia server</c> (#76): the property that matters is that a served analysis is the one this
/// process would have produced, before and after the tree changes under it.
/// </summary>
[Collection(nameof(FixtureCollection))]
public sealed class ServerTests(XunitFixtureRepository repository) : IDisposable
{
    // Outside the fixture, which every other test in the collection analyses.
    private readonly string _cache = Directory.CreateTempSubdirectory("tia-server-cache-").FullName;

    public void Dispose()
    {
        repository.Revert();
        Directory.Delete(_cache, recursive: true);
    }

    private AnalysisOptions Options() => new()
    {
        RepositoryRoot = repository.AnalysisRoot,
        BaseRef = "HEAD",
        SolutionPath = repository.SolutionPath,
        UseCache = false,
        FallbackFullOnError = false,
        CacheDirectory = _cache,
    };

    [Fact]
    public async Task A_served_analysis_selects_what_this_process_would_and_follows_edits()
    {
        var cache = _cache;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var host = new ServerHost(repository.AnalysisRoot, repository.SolutionPath, cache, TimeSpan.FromMinutes(5), TextWriter.Null);
        var serving = Task.Run(() => host.RunAsync(stop.Token));

        try
        {
            await WaitUntilServing(stop.Token);

            repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");
            await AssertSameAsInProcess(stop.Token);

            // An edit after the server loaded: refreshed, not stale.
            repository.Edit("Fixtures.Core/Calculator.cs", "public int Subtract(int a, int b) => a - b;", "public int Subtract(int a, int b) => a + -b;");
            await AssertSameAsInProcess(stop.Token);

            // A new file the workspace has never seen: reloaded.
            repository.Write("Fixtures.Core/Probe.cs", "namespace Fixtures.Core; public static class Probe { public static int Value() => 1; }");
            var served = await AssertSameAsInProcess(stop.Token);
            Assert.Contains(served.UntestedChanges, u => u.Symbol.Contains("Probe.Value", StringComparison.Ordinal));
        }
        finally
        {
            await ServerClient.SendAsync(repository.AnalysisRoot, repository.SolutionPath,
                new ServerRequest { Command = "stop", Version = ServerProtocol.ToolVersion }, null, CancellationToken.None);
            Assert.Equal(0, await serving);
        }

        Assert.False(File.Exists(ServerProtocol.StatePath(cache)), "a stopped server removes its state file");
    }

    [Fact]
    public async Task Analyses_that_arrive_together_are_all_served_in_turn()
    {
        // #95: the pipe used to accept one connection at a time, and a client gave up after two
        // seconds. A second command started while the server analysed loaded the solution itself.
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var host = new ServerHost(repository.AnalysisRoot, repository.SolutionPath, _cache, TimeSpan.FromMinutes(5), TextWriter.Null);
        var serving = Task.Run(() => host.RunAsync(stop.Token));

        try
        {
            await WaitUntilServing(stop.Token);

            // A file the workspace has never seen makes the first analysis reload it, which takes
            // several seconds: longer than the old two-second wait for a connection.
            repository.Write("Fixtures.Core/Probe.cs", "namespace Fixtures.Core; public static class Probe { public static int Value() => 1; }");

            var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => ServerClient.TryAnalyzeAsync(Options(), null, stop.Token)));

            Assert.All(outcomes, o => Assert.True(o is { Served: true }, "every analysis was served"));
            Assert.All(outcomes, o => Assert.Contains(o!.Report.UntestedChanges, u => u.Symbol.Contains("Probe.Value", StringComparison.Ordinal)));

            var status = await ServerClient.SendAsync(repository.AnalysisRoot, repository.SolutionPath,
                new ServerRequest { Command = "status", Version = ServerProtocol.ToolVersion }, null, stop.Token);
            Assert.Equal(3, status!.Requests);
            Assert.Equal(2, status.Loads);
            Assert.False(status.Busy);
        }
        finally
        {
            await ServerClient.SendAsync(repository.AnalysisRoot, repository.SolutionPath,
                new ServerRequest { Command = "stop", Version = ServerProtocol.ToolVersion }, null, CancellationToken.None);
            Assert.Equal(0, await serving);
        }
    }

    [Fact]
    public async Task A_client_of_another_version_stops_the_server()
    {
        // #95: an old server kept answering "version mismatch", and every command after an
        // upgrade analysed in-process until somebody restarted it.
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var host = new ServerHost(repository.AnalysisRoot, repository.SolutionPath, _cache, TimeSpan.FromMinutes(5), TextWriter.Null);
        var serving = Task.Run(() => host.RunAsync(stop.Token));

        await WaitUntilServing(stop.Token);

        var answer = await ServerClient.SendAsync(repository.AnalysisRoot, repository.SolutionPath,
            ServerRequest.Analyze(Options()) with { Version = "0.0.1-another" }, null, stop.Token);

        Assert.NotNull(answer);
        Assert.False(answer.Ok);
        Assert.Equal(ServerProtocol.VersionMismatch, answer.Error);
        Assert.True(answer.Stopping);
        Assert.Contains("has stopped", ServerProtocol.DescribeMismatch(answer), StringComparison.Ordinal);

        Assert.Equal(0, await serving.WaitAsync(TimeSpan.FromSeconds(30), stop.Token));
        Assert.False(File.Exists(ServerProtocol.StatePath(_cache)), "a server that stopped removes its state file, so later clients do not ask");
    }

    [Fact]
    public async Task A_second_server_for_the_same_solution_does_not_start()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var first = new ServerHost(repository.AnalysisRoot, repository.SolutionPath, _cache, TimeSpan.FromMinutes(5), TextWriter.Null);
        var serving = Task.Run(() => first.RunAsync(stop.Token));

        try
        {
            await WaitUntilServing(stop.Token);

            var log = new StringWriter();
            var second = new ServerHost(repository.AnalysisRoot, repository.SolutionPath, _cache, TimeSpan.FromMinutes(5), log);

            Assert.Equal(1, await second.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(30), stop.Token));
            Assert.Contains("already listens", log.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(ServerProtocol.StatePath(_cache)), "the refused server leaves the running one's state file alone");
        }
        finally
        {
            await ServerClient.SendAsync(repository.AnalysisRoot, repository.SolutionPath,
                new ServerRequest { Command = "stop", Version = ServerProtocol.ToolVersion }, null, CancellationToken.None);
            Assert.Equal(0, await serving);
        }
    }

    [Fact]
    public async Task No_server_means_no_answer_and_no_wait()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await ServerClient.TryAnalyzeAsync(Options(), null, CancellationToken.None);

        Assert.Null(outcome);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), "without a state file the client does not try to connect");
    }

    private async Task<AnalysisReport> AssertSameAsInProcess(CancellationToken cancellationToken)
    {
        var served = await ServerClient.TryAnalyzeAsync(Options(), null, cancellationToken);
        Assert.NotNull(served);

        var local = await new SolutionAnalyzer(Options()).AnalyzeAsync(cancellationToken);

        Assert.Equal(FixtureRepository.SelectedTests(local.Report), FixtureRepository.SelectedTests(served.Report));
        Assert.Equal(local.Report.Mode, served.Report.Mode);
        Assert.Equal(local.Report.Diff.ChangedSymbolCount, served.Report.Diff.ChangedSymbolCount);
        Assert.Equal(local.Report.UntestedChanges.Select(u => u.Symbol), served.Report.UntestedChanges.Select(u => u.Symbol));
        Assert.Equal(local.AllTests.Count, served.AllTests.Count);
        return served.Report;
    }

    private async Task WaitUntilServing(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await ServerClient.SendAsync(repository.AnalysisRoot, repository.SolutionPath,
                new ServerRequest { Command = "status", Version = ServerProtocol.ToolVersion }, null, cancellationToken);
            if (status is { Ok: true })
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }
    }
}

public sealed class ServerProtocolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tia-server-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_pipe_is_named_for_the_repository_and_solution_not_read_from_a_file()
    {
        var a = ServerProtocol.PipeName("/repo", "/repo/A.slnx");

        Assert.Equal(a, ServerProtocol.PipeName("/repo", "/repo/A.slnx"));
        Assert.NotEqual(a, ServerProtocol.PipeName("/repo", "/repo/B.slnx"));
        Assert.NotEqual(a, ServerProtocol.PipeName("/other", "/repo/A.slnx"));
        Assert.StartsWith("tia-", a, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_survives_the_trip_unchanged()
    {
        var report = new AnalysisReport
        {
            Mode = "selective",
            BaseRef = "origin/main",
            TotalTests = 10,
            SelectedTests = 2,
            ImpactedTests = 2,
            Widenings = [new WideningEvent("Reflection", "App", "uses reflection")],
            UntestedChanges = [new UntestedChange { Symbol = "A.B()", Project = "App", File = "src/A.cs" }],
            Projects =
            [
                new ProjectSelection
                {
                    Name = "App.Tests", ProjectPath = "/r/App.Tests.csproj", Framework = "XUnitV3", Runner = "VsTest",
                    TotalTests = 10, SelectedTests = 2, Filtered = true, FilterArguments = ["--filter", "x"], Tests = ["A", "B"],
                    FirstWave = new RunWave { TestCount = 1, FilterArguments = ["a"], RemainderFilterArguments = ["b"], LastFailed = true },
                },
            ],
            SkippedTests = [new SkippedTests { Project = "App.Tests", Tests = ["C"] }],
            ElapsedSeconds = 1.5,
        };

        var response = new ServerResponse { Ok = true, Version = "1", Report = report };
        var back = JsonSerializer.Deserialize<ServerResponse>(JsonSerializer.Serialize(response, ServerProtocol.Json), ServerProtocol.Json)!;

        Assert.Equal(report.ToJson(), back.Report!.ToJson());
    }

    [Fact]
    public void A_build_input_that_changed_requires_a_reload()
    {
        var project = Path.Combine(_root, "App.csproj");
        File.WriteAllText(project, "<Project />");
        var before = BuildInputs.Take(_root);

        Assert.False(BuildInputs.Take(_root).RequiresReopen(before, _ => true));

        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.True(BuildInputs.Take(_root).RequiresReopen(before, _ => true));
    }

    [Fact]
    public void A_restore_requires_a_reload()
    {
        var obj = Directory.CreateDirectory(Path.Combine(_root, "App", "obj")).FullName;
        var before = BuildInputs.Take(_root);

        File.WriteAllText(Path.Combine(obj, "project.assets.json"), "{}");

        Assert.True(BuildInputs.Take(_root).RequiresReopen(before, _ => true));
    }

    [Fact]
    public void An_edited_source_file_is_a_refresh_and_a_new_one_a_reload()
    {
        var source = Path.Combine(_root, "A.cs");
        File.WriteAllText(source, "class A { }");
        var before = BuildInputs.Take(_root);

        File.WriteAllText(source, "class A { int x; }");
        Assert.False(BuildInputs.Take(_root).RequiresReopen(before, _ => true));

        File.WriteAllText(Path.Combine(_root, "B.cs"), "class B { }");
        Assert.True(BuildInputs.Take(_root).RequiresReopen(before, path => !path.EndsWith("B.cs", StringComparison.Ordinal)));
    }

    [Fact]
    public void Build_output_is_not_a_build_input()
    {
        var before = BuildInputs.Take(_root);
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "Generated.cs"), "class G { }");
        File.WriteAllText(Path.Combine(bin, "x.props"), "<Project />");

        Assert.False(BuildInputs.Take(_root).RequiresReopen(before, _ => false));
    }
}
