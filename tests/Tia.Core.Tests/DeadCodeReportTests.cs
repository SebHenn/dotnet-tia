using System.Text.Json;
using Tia.Core.Configuration;
using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class DeadCodeReportTests
{
    private const string Source = """
        public static class Program
        {
            public static void Main() { }
            private static void Forgotten() { }
            private static void BuildAvaloniaApp() { }
        }
        """;

    private static DeadCodeReport Report(string configJson)
    {
        var compilation = CompilationHarness.CompileValid(Source);
        var graph = CompilationHarness.BuildGraph(compilation);
        var result = Reachability.Sweep(graph, Reachability.EntryPoints(graph));
        var directory = Path.GetDirectoryName(result.Unreached[0].FilePath!)!;

        return DeadCodeReport.Create(directory, graph, result, DeadCodeConfig.Parse(configJson, "deadcode.json"), roots: 1, tests: 0);
    }

    [Fact]
    public void A_symbol_pattern_takes_a_finding_out_of_the_list_and_into_the_ignored_count()
    {
        var report = Report("""{ "ignoreSymbols": ["*.BuildAvaloniaApp()"] }""");

        Assert.Equal(["Program.Forgotten()"], report.Findings.Select(finding => finding.Symbol));
        Assert.Equal(1, report.Ignored);
    }

    [Fact]
    public void A_path_pattern_ignores_every_finding_in_the_files_it_covers()
    {
        var unfiltered = Report("{}");
        var report = Report($$"""{ "ignorePaths": ["{{unfiltered.Findings[0].File}}"] }""");

        Assert.Empty(report.Findings);
        Assert.Equal(2, report.Ignored);
    }

    [Fact]
    public void The_json_report_carries_what_a_reader_needs_to_find_each_declaration()
    {
        using var json = JsonDocument.Parse(Report("""{ "ignoreSymbols": ["*.BuildAvaloniaApp()"] }""").ToJson());

        var finding = Assert.Single(json.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Equal("Program.Forgotten()", finding.GetProperty("symbol").GetString());
        Assert.Equal("method", finding.GetProperty("kind").GetString());
        Assert.Equal(4, finding.GetProperty("line").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("ignored").GetInt32());
    }

    [Theory]
    [InlineData("""{ "ignorePath": ["obj"] }""", "'ignorePath' is not a setting")]
    [InlineData("""{ "ignoreSymbols": "Program.*" }""", "has to be an array of strings")]
    [InlineData("""{ "ignorePaths": ["!src"] }""", "negation is not supported")]
    [InlineData("""{ "includePublic": "yes" }""", "has to be true or false")]
    public void A_config_that_would_silently_do_nothing_is_refused(string json, string expected)
    {
        var error = Assert.Throws<ConfigFileException>(() => DeadCodeConfig.Parse(json, "deadcode.json"));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }
}
