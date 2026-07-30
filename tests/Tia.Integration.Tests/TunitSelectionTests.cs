namespace Tia.Integration.Tests;

/// <summary>
/// Selection over a repository that has opted into the Microsoft.Testing.Platform
/// <c>dotnet test</c> command through <c>global.json</c>. That opt-in changes the shape of the
/// invocation - the project moves to <c>--project</c> and runner arguments lose their <c>--</c>
/// separator - so it is a separate code path from the VSTest command, not a variation of it.
/// </summary>
[Collection(nameof(TunitFixtureCollection))]
public sealed class TunitSelectionTests(TunitFixtureRepository repository) : IDisposable
{
    private const int TotalTests = 4;

    public void Dispose() => repository.Revert();

    [Fact]
    public async Task A_TUnit_project_gets_a_tree_node_filter()
    {
        repository.Edit("Tunit.Core/Clock.cs", "public bool IsMidnight() => Hour == 0;", "public bool IsMidnight() { return Hour == 0; }");

        var report = await repository.AnalyzeAsync();

        var project = Assert.Single(report.Projects);
        Assert.Equal("TUnit", project.Framework);
        Assert.Equal("MicrosoftTestingPlatform", project.Runner);
        Assert.True(project.Filtered);
        Assert.Equal(
            ["--treenode-filter", "/*/Tunit.Fixtures.Tests/ClockTests/Starts_at_midnight"],
            project.FilterArguments);
    }

    [Fact]
    public async Task The_global_json_opt_in_changes_the_dotnet_test_command()
    {
        var report = await repository.AnalyzeAsync();

        Assert.Equal("MicrosoftTestingPlatform", report.DotnetTestMode);
    }

    [Fact]
    public async Task A_selection_spanning_two_classes_becomes_a_segment_cross_product()
    {
        // The tree-node grammar alternates inside a path segment, not across whole paths, so two
        // classes and two method names describe four cells. A superset, never a subset.
        repository.Edit("Tunit.Core/Clock.cs", "public bool IsMidnight() => Hour == 0;", "public bool IsMidnight() { return Hour == 0; }");
        repository.Edit("Tunit.Core/Calendar.cs",
            "public bool IsLeapDay(int month, int day) => month == 2 && day == 29;",
            "public bool IsLeapDay(int month, int day) { return month == 2 && day == 29; }");

        var report = await repository.AnalyzeAsync();

        var project = Assert.Single(report.Projects);
        Assert.Equal(
            [
                "--treenode-filter",
                "/*/Tunit.Fixtures.Tests/CalendarTests|ClockTests/Recognises_a_leap_day|Starts_at_midnight",
            ],
            project.FilterArguments);
    }

    [Fact]
    public async Task Changing_a_file_in_a_generator_driven_project_widens_it()
    {
        // TUnit is a real Roslyn source generator: its generated registrations have no file on
        // disk, so a change to its input cannot be attributed at symbol granularity.
        repository.Edit("Tunit.Tests/ClockTests.cs", "var clock = new Clock();", "Clock clock = new();");

        var report = await repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w.Cause == "SourceGenerator");
        Assert.Equal(TotalTests, report.SelectedTests);
    }

    [Fact]
    public async Task An_untouched_diff_selects_nothing()
    {
        var report = await repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal(0, report.SelectedTests);
        Assert.Equal(TotalTests, report.TotalTests);
    }
}

[CollectionDefinition(nameof(TunitFixtureCollection))]
public sealed class TunitFixtureCollection : ICollectionFixture<TunitFixtureRepository>;
