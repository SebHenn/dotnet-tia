using System.Text.Json.Nodes;
using Tia.Cli;

namespace Tia.Integration.Tests;

/// <summary>
/// <c>analyze --write-filters</c>: each test project's filter as a file its own job passes to
/// <c>dotnet test</c>, and a manifest saying which projects to run (#159).
/// </summary>
/// <remarks>
/// For a pipeline that builds once and runs each test project in a job of its own, where neither
/// tia nor the checkout is available. Each job looks itself up in the manifest. The manifest has
/// to name every project the analysis knows, the skipped ones included, because a project it
/// leaves out is run whole.
/// </remarks>
[Collection(nameof(FixtureCollection))]
public sealed class FilterExportTests(XunitFixtureRepository repository) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tia-write-filters-").FullName;

    public void Dispose()
    {
        repository.Revert();
        Directory.Delete(_directory, recursive: true);
    }

    private string Filters => Path.Combine(_directory, "filters");

    private async Task<(JsonObject Manifest, JsonObject Report)> AnalyzeAsync()
    {
        var reportPath = Path.Combine(_directory, "tia-report.json");
        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);
        try
        {
            var exitCode = await Cli.InvokeAsync(
            [
                "analyze", "--base", "HEAD", "--no-cache", "--no-server", "--report", reportPath, "--write-filters", Filters,
                "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath,
            ]);
            Assert.Equal(ExitCodes.Ok, exitCode);
        }
        finally
        {
            Console.SetOut(previous);
        }

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Filters, FilterExport.ManifestName), TestContext.Current.CancellationToken))!.AsObject();
        var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken))!.AsObject();
        return (manifest, report);
    }

    private static JsonObject Entry(JsonObject manifest, string project) =>
        Assert.Single(manifest["projects"]!.AsArray(), p => p!["project"]!.GetValue<string>() == project)!.AsObject();

    private static IReadOnlyList<string> FilterArguments(JsonObject report, string project) =>
    [
        .. Assert.Single(report["projects"]!.AsArray(), p => p!["name"]!.GetValue<string>() == project)!["filterArguments"]!
            .AsArray().Select(a => a!.GetValue<string>()),
    ];

    private string Read(string name) => File.ReadAllText(Path.Combine(Filters, name));

    [Fact]
    public async Task A_selected_platform_project_gets_a_response_file_and_a_skipped_one_is_named_too()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");

        var (manifest, report) = await AnalyzeAsync();

        Assert.Equal(report["headCommit"]!.GetValue<string>(), manifest["headCommit"]!.GetValue<string>());
        Assert.Equal(report["toolVersion"]!.GetValue<string>(), manifest["toolVersion"]!.GetValue<string>());

        var tests = Entry(manifest, "Fixtures.Tests");
        Assert.Equal("filtered", tests["run"]!.GetValue<string>());
        Assert.Equal("Fixtures.Tests", tests["assembly"]!.GetValue<string>());
        Assert.Equal("Fixtures.Tests/Fixtures.Tests.csproj", tests["relativeProjectPath"]!.GetValue<string>());
        var responseFile = tests["responseFile"]!.GetValue<string>();
        Assert.Equal(
            string.Concat(FilterArguments(report, "Fixtures.Tests").Select(a => $"\"{a}\"\n")),
            Read(responseFile));

        // Nothing selected there, and said so: a project the manifest leaves out runs whole.
        var nunit = Entry(manifest, "Fixtures.NUnitTests");
        Assert.Equal("none", nunit["run"]!.GetValue<string>());
        Assert.Null(nunit["runSettings"]);
        Assert.Equal(3, nunit["totalTests"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_selected_VSTest_project_gets_run_settings_and_the_bare_filter()
    {
        repository.Edit("Fixtures.Core/Counter.cs", "public int Decrement() => --_value;", "public int Decrement() => _value -= 1;");

        var (manifest, report) = await AnalyzeAsync();

        var nunit = Entry(manifest, "Fixtures.NUnitTests");
        Assert.Equal("filtered", nunit["run"]!.GetValue<string>());
        var arguments = FilterArguments(report, "Fixtures.NUnitTests");
        Assert.Equal("--filter", arguments[0]);
        Assert.Equal(arguments[1] + "\n", Read(nunit["filter"]!.GetValue<string>()));
        Assert.Contains($"<TestCaseFilter>{arguments[1]}</TestCaseFilter>", Read(nunit["runSettings"]!.GetValue<string>()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_again_removes_the_files_of_the_last_selection()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");
        var (first, _) = await AnalyzeAsync();
        var stale = Entry(first, "Fixtures.Tests")["responseFile"]!.GetValue<string>();
        await File.WriteAllTextAsync(Path.Combine(Filters, "mine.txt"), "not tia's", TestContext.Current.CancellationToken);

        repository.Revert();
        repository.Edit("Fixtures.Core/Fixtures.Core.csproj", "<RootNamespace>Fixtures.Core</RootNamespace>",
            "<RootNamespace>Fixtures.Core</RootNamespace>\n    <NoWarn>CS0168</NoWarn>");
        var (second, _) = await AnalyzeAsync();

        // A job that looked for its file by name must not find the last selection's.
        Assert.False(File.Exists(Path.Combine(Filters, stale)));
        Assert.Equal("all", Entry(second, "Fixtures.Tests")["run"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(Filters, "mine.txt")));
    }

    [Fact]
    public async Task A_full_run_runs_every_project_whole_and_writes_no_filter()
    {
        repository.Edit("Fixtures.Core/Fixtures.Core.csproj", "<RootNamespace>Fixtures.Core</RootNamespace>",
            "<RootNamespace>Fixtures.Core</RootNamespace>\n    <NoWarn>CS0168</NoWarn>");

        var (manifest, _) = await AnalyzeAsync();

        Assert.Equal("full", manifest["mode"]!.GetValue<string>());
        var projects = manifest["projects"]!.AsArray();
        Assert.Contains(projects, p => p!["project"]!.GetValue<string>() == "Fixtures.Tests");
        Assert.Contains(projects, p => p!["project"]!.GetValue<string>() == "Fixtures.NUnitTests");
        Assert.All(projects, p => Assert.Equal("all", p!["run"]!.GetValue<string>()));
        Assert.Equal([FilterExport.ManifestName], Directory.GetFiles(Filters).Select(Path.GetFileName));
    }
}
