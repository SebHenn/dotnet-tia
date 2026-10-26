using System.Text.Json;
using Tia.Cli.Commands;
using Tia.Cli.Server;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// <c>explain --symbol</c> (#98): what reaches a symbol, and when nothing does, how close a test
/// gets. Over the fixture solution, so every answer is the selection's own traversal.
/// </summary>
[Collection(nameof(FixtureCollection))]
public sealed class SymbolQueryTests(XunitFixtureRepository repository) : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("tia-symbol-cache-").FullName;

    public void Dispose()
    {
        repository.Revert();
        Directory.Delete(_cache, recursive: true);
    }

    private AnalysisOptions Options() => repository.ServerOptions(_cache);

    [Fact]
    public async Task A_tested_symbol_lists_the_tests_a_change_to_it_would_select()
    {
        var outcome = await new SolutionAnalyzer(Options()).AnalyzeAsync(TestContext.Current.CancellationToken);

        var result = SymbolQuery.Run(outcome, "Calculator.Add", repository.AnalysisRoot, TestContext.Current.CancellationToken);

        var found = Assert.Single(result.Symbols);
        Assert.Equal("Calculator.Add(int, int)", found.Symbol);
        Assert.Equal("Fixtures.Core/Calculator.cs", found.File);
        Assert.Contains(found.Tests, t => t.Test == "Fixtures.Tests.CalculatorTests.Adds");
        Assert.Empty(found.Nearby);

        // The same tests the selection picks when the symbol really changes, less the one test
        // every selection carries because it constructs types by name.
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");
        var changed = await new SolutionAnalyzer(Options()).AnalyzeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            FixtureRepository.SelectedTests(changed.Report).Where(t => !t.Contains("ReflectiveFactoryTests", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
            found.Tests.Select(t => t.Test).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_untested_symbol_points_at_the_nearest_thing_a_test_reaches()
    {
        repository.Edit("Fixtures.Core/Calculator.cs",
            "public int Add(int a, int b) => a + b;",
            "public int Add(int a, int b) => a + b;\n    public int Multiply(int a, int b) => a * b;");

        var outcome = await new SolutionAnalyzer(Options()).AnalyzeAsync(TestContext.Current.CancellationToken);
        var found = Assert.Single(SymbolQuery.Run(outcome, "Multiply", repository.AnalysisRoot, TestContext.Current.CancellationToken).Symbols);

        Assert.Equal(0, found.TestCount);
        var nearest = found.Nearby[0];
        Assert.Equal("Calculator", nearest.Symbol);
        Assert.Equal(1, nearest.Distance);
        Assert.Equal("declared by", nearest.Route[^1].Via);
        Assert.StartsWith("Fixtures.Tests.CalculatorTests.", nearest.Test, StringComparison.Ordinal);
        Assert.All(found.Nearby, n => Assert.True(n.Distance <= SymbolQuery.MaxDistance));
    }

    [Fact]
    public async Task No_match_lists_what_contains_the_query()
    {
        var outcome = await new SolutionAnalyzer(Options()).AnalyzeAsync(TestContext.Current.CancellationToken);

        var result = SymbolQuery.Run(outcome, "Calcul", repository.AnalysisRoot, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Matched);
        Assert.Contains("Calculator.Add(int, int)", result.Containing);
    }

    [Fact]
    public async Task A_served_answer_is_the_one_this_process_gives()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var server = await RunningServer.StartAsync(repository, _cache, stop.Token);

        var local = SymbolQuery.Run(await new SolutionAnalyzer(Options()).AnalyzeAsync(stop.Token), "Calculator", repository.AnalysisRoot, stop.Token);
        var served = await ServerClient.TryExplainSymbolAsync(Options(), "Calculator", null, stop.Token);

        Assert.NotNull(served);
        Assert.Equal(JsonSerializer.Serialize(local, AnalysisReport.JsonOptions), JsonSerializer.Serialize(served, AnalysisReport.JsonOptions));
    }

    [Theory]
    [InlineData("Calculator.Add(int, int)", "Calculator.Add", true)]
    [InlineData("Calculator.Add(int, int)", "Add(int, int)", true)]
    [InlineData("Calculator.Add(int, int)", "add", true)]
    [InlineData("Calculator.Add(int, int)", "Calculator", false)]
    [InlineData("Calculator", "Calculator", true)]
    public void A_symbol_matches_by_a_suffix_with_or_without_its_parameters(string displayName, string query, bool matches)
    {
        Assert.Equal(matches, SymbolQuery.Matches(displayName, query));
    }
}
