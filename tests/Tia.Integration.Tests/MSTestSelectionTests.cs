namespace Tia.Integration.Tests;

/// <summary>
/// MSTest, selected from and executed (#101). xUnit, NUnit and TUnit each had a fixture whose
/// emitted filter the suite runs; MSTest had attribute-discovery tests and nothing that checked a
/// runner agreed with the filter. Two trees, because its own runner and VSTest are two different
/// commands: the shared cases run against both.
/// </summary>
public abstract class MSTestSelectionTests(FixtureRepository repository, string project, string runner, string ns) : IDisposable
{
    public void Dispose() => repository.Revert();

    [Fact]
    public async Task An_MSTest_project_is_detected_with_its_runner()
    {
        repository.Edit("MSTest.Core/Ledger.cs", "public int Deposit(int amount) => _balance += amount;", "public int Deposit(int amount) { return _balance += amount; }");

        var report = await repository.AnalyzeAsync();

        var selection = Assert.Single(report.Projects);
        Assert.Equal(project, selection.Name);
        Assert.Equal("MSTest", selection.Framework);
        Assert.Equal(runner, selection.Runner);
        Assert.True(selection.WritesTrx);
    }

    [Fact]
    public async Task A_method_change_selects_and_runs_exactly_its_test()
    {
        repository.Edit("MSTest.Core/Ledger.cs", "public int Deposit(int amount) => _balance += amount;", "public int Deposit(int amount) { return _balance += amount; }");

        var report = await repository.AnalyzeAsync();

        Assert.Equal([$"{ns}.LedgerTests.Deposits"], FixtureRepository.SelectedTests(report));
        Assert.Equal([$"{ns}.LedgerTests.Deposits"], repository.RunAndCollectExecuted(report, project));
    }

    [Fact]
    public async Task A_test_initialize_method_reaches_every_test_in_its_class()
    {
        // Nothing calls CreateLedger; only the fixture edge connects it to the tests it serves.
        repository.Edit($"{TestsDirectory}/Tests.cs",
            "_ledger = new Ledger();", "_ledger = new Ledger() { };");

        var report = await repository.AnalyzeAsync();

        string[] ledgerTests =
        [
            $"{ns}.LedgerTests.Charges_a_fee",
            $"{ns}.LedgerTests.Deposits",
            $"{ns}.LedgerTests.Is_overdrawn_after_a_charge",
            $"{ns}.LedgerTests.Withdraws",
        ];
        Assert.Equal(ledgerTests, FixtureRepository.SelectedTests(report).Order(StringComparer.Ordinal));
        Assert.Equal(ledgerTests, repository.RunAndCollectExecuted(report, project));
    }

    [Fact]
    public async Task What_a_class_initialize_calls_reaches_the_whole_class_and_a_data_row_test_runs_whole()
    {
        repository.Edit("MSTest.Core/Ledger.cs", "public static string Prefix() => \"EUR \";", "public static string Prefix() { return \"EUR \"; }");

        var report = await repository.AnalyzeAsync();

        string[] formatterTests =
        [
            $"{ns}.FormatterTests.Formats_money",
            $"{ns}.FormatterTests.Formats_negative_money",
            $"{ns}.FormatterTests.Formats_percent",
        ];
        Assert.Equal(formatterTests, FixtureRepository.SelectedTests(report).Order(StringComparer.Ordinal));
        Assert.Equal(formatterTests, repository.RunAndCollectExecuted(report, project));
    }

    [Fact]
    public async Task A_filter_too_long_for_the_command_line_goes_in_a_file_and_runs_exactly_the_selection()
    {
        repository.Edit("MSTest.Core/Ledger.cs", "public int Withdraw(int amount) => _balance -= amount;", "public int Withdraw(int amount) { return _balance -= amount; }");

        var report = await repository.AnalyzeAsync(maxFilterLength: 10);
        var selection = Assert.Single(report.Projects);

        Assert.True(selection.Filtered);
        Assert.True(selection.FilterInFile);
        Assert.Equal([$"{ns}.LedgerTests.Withdraws"], repository.RunAndCollectExecuted(report, project));
    }

    protected abstract string TestsDirectory { get; }
}

[Collection(nameof(MSTestFixtureCollection))]
public sealed class MSTestOnVsTestSelectionTests(MSTestFixtureRepository repository)
    : MSTestSelectionTests(repository, "MSTest.Tests", "VsTest", "MSTest.Fixtures.Tests")
{
    protected override string TestsDirectory => "MSTest.Tests";
}

[Collection(nameof(MSTestRunnerFixtureCollection))]
public sealed class MSTestRunnerSelectionTests(MSTestRunnerFixtureRepository repository)
    : MSTestSelectionTests(repository, "MSTest.RunnerTests", "MicrosoftTestingPlatform", "MSTest.Fixtures.RunnerTests")
{
    protected override string TestsDirectory => "MSTest.RunnerTests";
}

[CollectionDefinition(nameof(MSTestFixtureCollection))]
public sealed class MSTestFixtureCollection : ICollectionFixture<MSTestFixtureRepository>;

[CollectionDefinition(nameof(MSTestRunnerFixtureCollection))]
public sealed class MSTestRunnerFixtureCollection : ICollectionFixture<MSTestRunnerFixtureRepository>;
