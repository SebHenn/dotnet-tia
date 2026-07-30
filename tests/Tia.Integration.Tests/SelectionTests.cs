namespace Tia.Integration.Tests;

/// <summary>
/// End-to-end selection over the fixture solution: real git, real MSBuild, real filters. These
/// assert the exact expected test set, which is the only way to catch over-selection - a tool that
/// quietly selects everything passes every "no misses" check there is.
/// </summary>
[Collection(nameof(FixtureCollection))]
public sealed class SelectionTests(XunitFixtureRepository repository) : IDisposable
{
    private const int TotalTests = 12;

    private const int XunitTests = 9;

    /// <summary>
    /// Runs on every selection that has anything to select. <c>ReflectiveFactory</c> resolves a
    /// type by name, so a change anywhere could alter what it constructs and no static edge would
    /// show it - the only sound reading is that its test is always impacted. Spelling it into each
    /// expectation, rather than hiding it in a helper, keeps the cost of that decision visible.
    /// </summary>
    private const string AlwaysReflective = "Fixtures.Tests.ReflectiveFactoryTests.Describes_a_type_it_only_knows_by_name";

    public void Dispose() => repository.Revert();

    [Fact]
    public async Task An_ordinary_method_change_selects_only_its_own_test()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");

        var report = await repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal(["Fixtures.Tests.CalculatorTests.Adds", AlwaysReflective], FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task Interface_dispatch_connects_an_implementation_to_the_code_that_only_sees_the_interface()
    {
        repository.Edit("Fixtures.Core/Greeting.cs", """$"Hello, {name}";""", """$"Hi, {name}";""");

        var report = await repository.AnalyzeAsync();

        // GreeterService only ever names IGreeter, so the interface edge is the only path.
        // GermanGreeterTests names the sibling implementation directly, and nothing about a change
        // to EnglishGreeter says anything about it - going up to the interface and straight back
        // down would claim otherwise.
        Assert.Equal(
            ["Fixtures.Tests.GreeterServiceTests.Welcomes_through_the_interface", AlwaysReflective],
            FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task Changing_an_implementation_no_test_can_reach_selects_only_its_own_test()
    {
        // GreeterServiceTests injects an EnglishGreeter. It calls IGreeter.Greet, so it reaches
        // the interface member - but nothing in it can ever get hold of a GermanGreeter, so it
        // cannot dispatch to one. Reaching the interface is not enough on its own.
        repository.Edit("Fixtures.Core/Greeting.cs", """$"Hallo, {name}";""", """$"Servus, {name}";""");

        var report = await repository.AnalyzeAsync();

        Assert.Equal(
            ["Fixtures.Tests.GermanGreeterTests.Greets_in_german", AlwaysReflective],
            FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task One_half_of_a_partial_class_selects_only_the_test_for_that_half()
    {
        repository.Edit("Fixtures.Core/Splitter.Part2.cs", "return parts[^1];", "return parts[parts.Length - 1];");

        var report = await repository.AnalyzeAsync();

        Assert.Equal([AlwaysReflective, "Fixtures.Tests.SplitterTests.Takes_the_last"], FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task An_open_generic_is_reached_through_its_constructed_form()
    {
        repository.Edit("Fixtures.Core/Box.cs", "public void Put(T value) => _value = value;", "public void Put(T value) { _value = value; }");

        var report = await repository.AnalyzeAsync();

        Assert.Equal(["Fixtures.Tests.BoxTests.Round_trips", AlwaysReflective], FixtureRepository.SelectedTests(report));
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
    public async Task A_type_reachable_only_by_reflection_still_selects_the_test_that_runs_it()
    {
        // Nothing statically references Widget - the factory names it as a string - and Widget
        // lives in its own file, so this is not the easy version of the case: the changed file
        // holds no reflection at all, and nothing the traversal reaches does either. The test is
        // selected only because every reflecting member in the solution is seeded, whether or not
        // anything reaches it. Scanning reached files alone missed exactly this on NodaTime.
        repository.Edit("Fixtures.Core/Widget.cs", """public override string ToString() => "widget";""",
            """public override string ToString() => "a widget";""");

        var report = await repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w.Cause == "Reflection");
        Assert.Contains(AlwaysReflective, FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task Reflection_is_reported_but_does_not_drag_in_unrelated_tests()
    {
        repository.Edit("Fixtures.Core/ReflectiveFactory.cs",
            "var instance = type is null ? null : Activator.CreateInstance(type);",
            "object? instance = type is null ? null : Activator.CreateInstance(type);");

        var report = await repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w.Cause == "Reflection");
        Assert.Equal([AlwaysReflective], FixtureRepository.SelectedTests(report));
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

        var project = report.Projects.Single(p => p.Name == "Fixtures.Tests");
        Assert.Equal("XUnitV3", project.Framework);
        Assert.Equal("MicrosoftTestingPlatform", project.Runner);
        Assert.True(project.Filtered);
        Assert.Equal(
            ["--filter-method", "Fixtures.Tests.CalculatorTests.Adds", "--filter-method", AlwaysReflective],
            project.FilterArguments);
    }

    [Fact]
    public async Task An_NUnit_project_on_the_VSTest_bridge_gets_VSTest_syntax()
    {
        repository.Edit("Fixtures.Core/Counter.cs", "public int Decrement() => --_value;", "public int Decrement() { return --_value; }");

        var report = await repository.AnalyzeAsync();

        var project = report.Projects.Single(p => p.Name == "Fixtures.NUnitTests");
        Assert.Equal("NUnit", project.Framework);
        Assert.Equal("VsTest", project.Runner);
        Assert.True(project.Filtered);
        Assert.Equal(["--filter", "FullyQualifiedName~Fixtures.NUnitTests.CounterTests.Decrements"], project.FilterArguments);
    }

    [Fact]
    public async Task The_NUnit_filter_runs_exactly_the_selected_tests()
    {
        // Asserting the argument list is not the same as asserting the runner agrees with it. The
        // xUnit dialect once emitted a perfectly reasonable-looking filter that selected nothing,
        // and only executing it found that out - so every dialect earns an execution test, not
        // just the one that was caught.
        repository.Edit("Fixtures.Core/Counter.cs", "public int Decrement() => --_value;", "public int Decrement() => _value -= 1;");

        var report = await repository.AnalyzeAsync();
        var project = report.Projects.Single(p => p.Name == "Fixtures.NUnitTests");

        Assert.Equal("NUnit", project.Framework);
        Assert.NotEmpty(project.Tests);
        Assert.Equal(project.Tests, repository.RunAndCollectExecuted(report, project.Name));
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
                AlwaysReflective,
            ],
            FixtureRepository.SelectedTests(report).Order(StringComparer.Ordinal));
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
    public async Task The_emitted_filter_runs_exactly_the_selected_tests()
    {
        // Selects one whole class and part of another, which is the shape that tempts a dialect
        // into mixing filter kinds. Running it is the only way to find out whether it worked.
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Subtract(int a, int b) => a - b;", "public int Subtract(int a, int b) => a + -b;");
        repository.Edit("Fixtures.Core/Splitter.Part2.cs", "return parts[^1];", "return parts[parts.Length - 1];");

        var report = await repository.AnalyzeAsync();
        var selected = FixtureRepository.SelectedTests(report);

        Assert.Equal(
            [
                "Fixtures.Tests.CalculatorTests.Adds",
                "Fixtures.Tests.CalculatorTests.Subtracts",
                AlwaysReflective,
                "Fixtures.Tests.SplitterTests.Takes_the_last",
            ],
            selected);

        Assert.Equal(selected, repository.RunAndCollectExecuted(report, "Fixtures.Tests"));
    }

    [Fact]
    public async Task A_fully_selected_class_collapses_and_still_runs_exactly_those_tests()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Subtract(int a, int b) => a - b;", "public int Subtract(int a, int b) => a + -b;");

        var report = await repository.AnalyzeAsync();

        // The reflective test is always in the selection, so CalculatorTests is no longer the
        // only class in it and the collapse now has to coexist with a method filter.
        var project = report.Projects.Single(p => p.Name == "Fixtures.Tests");
        Assert.Equal(
            ["Fixtures.Tests.CalculatorTests.Adds", "Fixtures.Tests.CalculatorTests.Subtracts", AlwaysReflective],
            repository.RunAndCollectExecuted(report, "Fixtures.Tests"));
        Assert.True(project.Filtered);
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
        Assert.Equal(["Fixtures.Tests.CalculatorTests.Adds", AlwaysReflective], FixtureRepository.SelectedTests(second));
    }

    [Fact]
    public async Task A_comment_only_change_selects_nothing()
    {
        repository.Edit(
            "Fixtures.Core/Calculator.cs",
            "public int Add(int a, int b) => a + b;",
            "// addition, in case that was unclear\n    public int Add(int a, int b) => a + b;");

        var report = await repository.AnalyzeAsync();

        Assert.Empty(FixtureRepository.SelectedTests(report));
        Assert.Contains(report.Diagnostics, d => d.Contains("only comments or formatting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_comment_next_to_a_real_change_does_not_hide_it()
    {
        repository.Edit(
            "Fixtures.Core/Calculator.cs",
            "public int Add(int a, int b) => a + b;",
            "// addition\n    public int Add(int a, int b) => b + a;");

        var report = await repository.AnalyzeAsync();

        Assert.Equal(["Fixtures.Tests.CalculatorTests.Adds", AlwaysReflective], FixtureRepository.SelectedTests(report));
    }
}

[CollectionDefinition(nameof(FixtureCollection))]
public sealed class FixtureCollection : ICollectionFixture<XunitFixtureRepository>;
