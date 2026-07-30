namespace Tia.Integration.Tests;

/// <summary>
/// End-to-end selection over the fixture solution: real git, real MSBuild, real filters. These
/// assert the exact expected test set, which is the only way to catch over-selection - a tool that
/// quietly selects everything passes every "no misses" check there is.
/// </summary>
[Collection(nameof(FixtureCollection))]
public sealed class SelectionTests(FixtureRepository repository) : IDisposable
{
    private const int TotalTests = 11;

    private const int XunitTests = 8;

    public void Dispose() => repository.Revert();

    [Fact]
    public async Task An_ordinary_method_change_selects_only_its_own_test()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");

        var report = await repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal(["Fixtures.Tests.CalculatorTests.Adds"], FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task Interface_dispatch_connects_an_implementation_to_the_code_that_only_sees_the_interface()
    {
        repository.Edit("Fixtures.Core/Greeting.cs", """$"Hello, {name}";""", """$"Hi, {name}";""");

        var report = await repository.AnalyzeAsync();

        Assert.Contains("Fixtures.Tests.GreeterServiceTests.Welcomes_through_the_interface", FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task One_half_of_a_partial_class_selects_only_the_test_for_that_half()
    {
        repository.Edit("Fixtures.Core/Splitter.Part2.cs", "return parts[^1];", "return parts[parts.Length - 1];");

        var report = await repository.AnalyzeAsync();

        Assert.Equal(["Fixtures.Tests.SplitterTests.Takes_the_last"], FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task An_open_generic_is_reached_through_its_constructed_form()
    {
        repository.Edit("Fixtures.Core/Box.cs", "public void Put(T value) => _value = value;", "public void Put(T value) { _value = value; }");

        var report = await repository.AnalyzeAsync();

        Assert.Equal(["Fixtures.Tests.BoxTests.Round_trips"], FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task Changing_a_constant_widens_to_the_whole_project_and_says_so()
    {
        repository.Edit("Fixtures.Core/Limits.cs", "public const int MaxRetries = 3;", "public const int MaxRetries = 4;");

        var report = await repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w.Cause == "ConstantInlining");
        Assert.Equal(TotalTests, report.SelectedTests);
    }

    [Fact]
    public async Task Reflection_in_a_changed_file_widens_to_the_whole_project_and_says_so()
    {
        repository.Edit("Fixtures.Core/ReflectiveFactory.cs", "return type is null ? null : Activator.CreateInstance(type);",
            "if (type is null) { return null; } return Activator.CreateInstance(type);");

        var report = await repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w.Cause == "Reflection");
        Assert.Equal(TotalTests, report.SelectedTests);
    }

    [Fact]
    public async Task A_project_file_change_bails_out_to_a_full_run()
    {
        repository.Edit("Fixtures.Core/Fixtures.Core.csproj", "<RootNamespace>Fixtures.Core</RootNamespace>",
            "<RootNamespace>Fixtures.Core</RootNamespace>\n    <NoWarn>CS0168</NoWarn>");

        var report = await repository.AnalyzeAsync();

        Assert.Equal("full", report.Mode);
        Assert.Contains(report.FullRunReasons, r => r.Contains("Fixtures.Core.csproj", StringComparison.Ordinal));
        Assert.Equal(TotalTests, report.SelectedTests);
    }

    [Fact]
    public async Task A_test_data_file_widens_its_project()
    {
        repository.Write("Fixtures.Tests/testdata.json", """{ "cases": [1, 2, 3] }""");

        var report = await repository.AnalyzeAsync();

        // The file belongs to the xUnit project, so only that project widens - nothing depends
        // on a test project.
        Assert.Contains(report.Widenings, w => w.Cause == "ContentFile");
        Assert.Equal(XunitTests, report.SelectedTests);
    }

    [Fact]
    public async Task An_untouched_diff_selects_nothing()
    {
        var report = await repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal(0, report.SelectedTests);
        Assert.Equal(TotalTests, report.TotalTests);
    }

    [Fact]
    public async Task The_emitted_filter_is_the_dialect_the_runner_needs()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");

        var report = await repository.AnalyzeAsync();

        var project = Assert.Single(report.Projects);
        Assert.Equal("XUnitV3", project.Framework);
        Assert.Equal("MicrosoftTestingPlatform", project.Runner);
        Assert.True(project.Filtered);
        Assert.Equal(["--filter-method", "Fixtures.Tests.CalculatorTests.Adds"], project.FilterArguments);
    }

    [Fact]
    public async Task An_NUnit_project_on_the_VSTest_bridge_gets_VSTest_syntax()
    {
        repository.Edit("Fixtures.Core/Counter.cs", "public int Decrement() => --_value;", "public int Decrement() { return --_value; }");

        var report = await repository.AnalyzeAsync();

        var project = Assert.Single(report.Projects);
        Assert.Equal("NUnit", project.Framework);
        Assert.Equal("VsTest", project.Runner);
        Assert.True(project.Filtered);
        Assert.Equal(["--filter", "FullyQualifiedName~Fixtures.NUnitTests.CounterTests.Decrements"], project.FilterArguments);
    }

    [Fact]
    public async Task An_NUnit_SetUp_method_reaches_every_test_in_its_class()
    {
        // Nothing calls CreateCounter; only the fixture edge connects it to the tests it serves.
        repository.Edit("Fixtures.NUnitTests/CounterTests.cs", "_counter = new Counter();", "_counter = new Counter() { };");

        var report = await repository.AnalyzeAsync();

        Assert.Equal(
            [
                "Fixtures.NUnitTests.CounterTests.Decrements",
                "Fixtures.NUnitTests.CounterTests.Increments",
                "Fixtures.NUnitTests.CounterTests.Increments_repeatedly",
            ],
            FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task A_parameterised_test_is_selected_whole()
    {
        repository.Edit("Fixtures.Core/Counter.cs", "public int Increment() => ++_value;", "public int Increment() { return ++_value; }");

        var report = await repository.AnalyzeAsync();

        // Two [TestCase]s, one entry: sub-case selection is not expressible in any dialect.
        Assert.Contains("Fixtures.NUnitTests.CounterTests.Increments_repeatedly", FixtureRepository.SelectedTests(report));
        Assert.Single(FixtureRepository.SelectedTests(report), t => t.EndsWith("Increments_repeatedly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_second_run_reuses_the_cached_graph()
    {
        await repository.AnalyzeAsync(useCache: true);

        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");
        var second = await repository.AnalyzeAsync(useCache: true);

        // Fixtures.Core changed, so it and its dependent Fixtures.Tests are rebuilt; a solution
        // with untouched projects would reuse those.
        Assert.True(second.Graph.ProjectsRebuilt > 0);
        Assert.Equal(["Fixtures.Tests.CalculatorTests.Adds"], FixtureRepository.SelectedTests(second));
    }
}

[CollectionDefinition(nameof(FixtureCollection))]
public sealed class FixtureCollection : ICollectionFixture<FixtureRepository>;
