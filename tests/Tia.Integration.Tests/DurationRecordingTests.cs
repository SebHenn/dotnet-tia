using System.Text.Json;
using Tia.Cli;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary><c>run --record-durations</c> and <c>--shard-durations</c> through the real commands (#91).</summary>
[Collection(nameof(FixtureCollection))]
public sealed class DurationRecordingTests(XunitFixtureRepository repository) : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("tia-durations-").FullName;

    public void Dispose()
    {
        repository.Revert();
        Directory.Delete(_cache, recursive: true);
    }

    [Fact]
    public async Task A_run_records_what_its_selected_tests_took()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");

        var exitCode = await Cli.InvokeAsync(
            "run", "--base", "HEAD", "--no-server", "--record-durations", "--cache-dir", _cache,
            "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath);

        Assert.Equal(ExitCodes.Ok, exitCode);
        var record = TestDurations.Read(TestDurations.PathIn(_cache), out var error);
        Assert.Null(error);
        Assert.True(record!.Seconds.ContainsKey("Fixtures.Tests.CalculatorTests.Adds"), string.Join(", ", record.Seconds.Keys));
        Assert.DoesNotContain("Fixtures.Tests.CalculatorTests.Subtracts", record.Seconds.Keys);
    }

    [Fact]
    public async Task A_shard_given_a_record_says_so_and_one_it_cannot_read_is_refused()
    {
        var path = Path.Combine(_cache, "durations.json");
        TestDurations.Write(path, new Dictionary<string, double> { ["Fixtures.Tests.CalculatorTests.Adds"] = 2.0 });

        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);
        int exitCode;
        try
        {
            exitCode = await Cli.InvokeAsync(
                "analyze", "--full", "--no-server", "--json", "--shard", "1/2", "--shard-durations", path,
                "--cache-dir", _cache, "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal(ExitCodes.Ok, exitCode);
        var shard = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("shard");
        Assert.Equal("durations", shard.GetProperty("weighting").GetString());
        Assert.Equal(TestDurations.Read(path, out _)!.Fingerprint, shard.GetProperty("durationsFingerprint").GetString());

        // Every shard must divide by the same record, so one that cannot read it does not guess.
        var refused = await Cli.InvokeAsync(
            "analyze", "--full", "--no-server", "--shard", "1/2", "--shard-durations", Path.Combine(_cache, "missing.json"),
            "--cache-dir", _cache, "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath);

        Assert.Equal(ExitCodes.Inconclusive, refused);
    }
}
