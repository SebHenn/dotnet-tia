using System.Text.Json.Nodes;
using Tia.Cli;

namespace Tia.Integration.Tests;

/// <summary>
/// <c>run --from-report</c>: one job analyses, the shards and the OS matrix run its report (#129).
/// </summary>
/// <remarks>
/// Every leg used to analyse the same commit again, cold. A report's run has to be the run the
/// analysis would have made, command for command, and a report that cannot promise that - another
/// commit, another version, a shard it cannot divide - is refused rather than run on a guess.
/// </remarks>
[Collection(nameof(FixtureCollection))]
public sealed class FromReportTests(XunitFixtureRepository repository) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tia-from-report-").FullName;

    private string ReportPath => Path.Combine(_directory, "tia-report.json");

    public void Dispose()
    {
        repository.Revert();
        Directory.Delete(_directory, recursive: true);
    }

    private string[] Where => ["--no-server", "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath];

    private async Task<int> AnalyzeAsync(params string[] extra)
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");
        var (exitCode, _, _) = await InvokeAsync(["analyze", "--base", "HEAD", "--no-cache", "--report", ReportPath, .. extra, .. Where]);
        return exitCode;
    }

    private static async Task<(int ExitCode, string Out, string Error)> InvokeAsync(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await Cli.InvokeAsync(args);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>The <c>dotnet test</c> lines of a dry run: what it would execute.</summary>
    private static IReadOnlyList<string> Commands(string output) =>
        [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("> dotnet test", StringComparison.Ordinal))];

    [Theory]
    [InlineData(null)]
    [InlineData("1/2")]
    [InlineData("2/2")]
    public async Task A_report_runs_what_a_fresh_analysis_would(string? shard)
    {
        Assert.Equal(ExitCodes.Ok, await AnalyzeAsync("--report-inventory"));
        string[] sharding = shard is null ? [] : ["--shard", shard];

        var fresh = await InvokeAsync(["run", "--dry-run", "--base", "HEAD", "--no-cache", .. sharding, .. Where]);
        var fromReport = await InvokeAsync(["run", "--dry-run", "--from-report", ReportPath, .. sharding, .. Where]);

        Assert.Equal(ExitCodes.Ok, fromReport.ExitCode);
        Assert.NotEmpty(Commands(fresh.Out));
        Assert.Equal(Commands(fresh.Out), Commands(fromReport.Out));
    }

    [Theory]
    [InlineData("headCommit", "0000000000000000000000000000000000000000", "HEAD here is")]
    [InlineData("toolVersion", "0.0.1", "was written by dotnet-tia 0.0.1")]
    [InlineData("toolVersion", null, "does not say which dotnet-tia wrote it")]
    [InlineData("schemaVersion", 2, "schema version 1")]
    public async Task A_report_from_another_commit_or_version_is_refused(string field, object? value, string reason)
    {
        Assert.Equal(ExitCodes.Ok, await AnalyzeAsync());
        var json = JsonNode.Parse(await File.ReadAllTextAsync(ReportPath, TestContext.Current.CancellationToken))!.AsObject();
        if (value is null)
        {
            json.Remove(field);
        }
        else
        {
            json[field] = value switch
            {
                int number => JsonValue.Create(number),
                _ => JsonValue.Create((string)value),
            };
        }

        await File.WriteAllTextAsync(ReportPath, json.ToJsonString(), TestContext.Current.CancellationToken);

        var result = await InvokeAsync(["run", "--dry-run", "--from-report", ReportPath, .. Where]);

        Assert.Equal(ExitCodes.Inconclusive, result.ExitCode);
        Assert.Contains("Cannot run this report", result.Error, StringComparison.Ordinal);
        Assert.Contains(reason, result.Error, StringComparison.Ordinal);
        Assert.Empty(Commands(result.Out));
    }

    [Fact]
    public async Task A_report_without_an_inventory_is_run_but_not_sharded()
    {
        Assert.Equal(ExitCodes.Ok, await AnalyzeAsync());

        Assert.Equal(ExitCodes.Ok, (await InvokeAsync(["run", "--dry-run", "--from-report", ReportPath, .. Where])).ExitCode);

        var sharded = await InvokeAsync(["run", "--dry-run", "--from-report", ReportPath, "--shard", "1/2", .. Where]);
        Assert.Equal(ExitCodes.Inconclusive, sharded.ExitCode);
        Assert.Contains("--report-inventory", sharded.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_written_in_another_checkout_finds_its_projects_in_this_one()
    {
        // Another job, another path, maybe another OS: only the relative path still holds.
        Assert.Equal(ExitCodes.Ok, await AnalyzeAsync());
        var json = JsonNode.Parse(await File.ReadAllTextAsync(ReportPath, TestContext.Current.CancellationToken))!.AsObject();
        foreach (var project in json["projects"]!.AsArray())
        {
            Assert.NotNull(project!["relativeProjectPath"]);
            project["projectPath"] = "/home/runner/work/elsewhere/" + project["relativeProjectPath"]!.GetValue<string>();
        }

        await File.WriteAllTextAsync(ReportPath, json.ToJsonString(), TestContext.Current.CancellationToken);

        var result = await InvokeAsync(["run", "--dry-run", "--from-report", ReportPath, .. Where]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        var command = Assert.Single(Commands(result.Out));
        Assert.Contains(Path.Combine(repository.AnalysisRoot, "Fixtures.Tests", "Fixtures.Tests.csproj"), command, StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere", command, StringComparison.Ordinal);
    }
}
