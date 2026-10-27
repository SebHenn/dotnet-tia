using Tia.Cli;
using Tia.Cli.Commands;
using Tia.Core.Reporting;
using Tia.Core.Validation;

namespace Tia.Integration.Tests;

/// <summary>
/// <c>run --results-directory</c> keeps each project's TRX for a test reporter or cifail (#118).
/// </summary>
/// <remarks>
/// <c>run</c> already asked every runner for TRX in its own dialect, but only to read its own
/// bookkeeping out of a temporary directory it then deleted. A caller who wanted the results had to
/// write <c>-- --logger trx</c>, which is wrong for Microsoft.Testing.Platform and turns off the
/// build that runs alongside the analysis.
/// </remarks>
[Collection(nameof(FixtureCollection))]
public sealed class ResultsDirectoryTests(XunitFixtureRepository repository) : IDisposable
{
    private readonly string _results = Path.Combine(Directory.CreateTempSubdirectory("tia-kept-").FullName, "TestResults");

    public void Dispose()
    {
        repository.Revert();
        Directory.Delete(Path.GetDirectoryName(_results)!, recursive: true);
    }

    [Fact]
    public async Task A_run_leaves_each_projects_results_in_a_folder_named_after_it()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");

        var exitCode = await Cli.InvokeAsync(
            "run", "--base", "HEAD", "--no-server", "--no-cache", "--results-directory", _results,
            "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath);

        Assert.Equal(ExitCodes.Ok, exitCode);
        var reports = Directory.GetFiles(Path.Combine(_results, "Fixtures.Tests"), "*.trx", SearchOption.AllDirectories);
        var outcomes = Assert.Single(reports) is { } trx ? TrxParser.Parse(trx) : [];
        Assert.Contains(outcomes, o => o.Name.Contains("CalculatorTests.Adds", StringComparison.Ordinal) && o.Result == "Passed");
    }

    [Theory]
    [InlineData("App.Tests", null, null, "App.Tests")]
    [InlineData("App.Tests(net8.0)", "net8.0", null, "App.Tests-net8.0")]
    [InlineData("App.Tests", null, 2, "App.Tests-shard-2")]
    [InlineData("App.Tests(net48)", "net48", 3, "App.Tests-net48-shard-3")]
    public void Instances_and_shards_never_share_a_folder(string name, string? framework, int? shard, string expected)
    {
        var project = new ProjectSelection
        {
            Name = name,
            ProjectPath = "/repo/App.Tests.csproj",
            Framework = "XUnitV3",
            Runner = "MicrosoftTestingPlatform",
            TotalTests = 1,
            SelectedTests = 1,
            Filtered = false,
            TargetFramework = framework,
        };

        Assert.Equal(expected, SelectiveRun.ResultsFolder(project, shard is { } index ? new ShardInfo { Index = index, Count = 4, SelectedAcrossShards = 1 } : null));
    }
}
