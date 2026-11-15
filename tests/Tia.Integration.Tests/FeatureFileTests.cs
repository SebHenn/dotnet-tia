namespace Tia.Integration.Tests;

/// <summary>
/// The fixture solution with a feature file and the class a Reqnroll build generates from it,
/// written by hand in the shape Reqnroll emits (xUnit traits naming the feature and each scenario),
/// committed so that a change to the feature is the whole diff (#172).
/// </summary>
public sealed class FeatureFilesFixture : IDisposable
{
    public XunitFixtureRepository Repository { get; } = new();

    public FeatureFilesFixture()
    {
        File.WriteAllText(
            Repository.PathOf("Fixtures.Tests", "Greeting.feature"),
            "Feature: Greeting\n  Scenario: Say hello\n    Given a name\n  Scenario: Say goodbye\n    Given a name\n");
        File.WriteAllText(
            Repository.PathOf("Fixtures.Tests", "Greeting.feature.cs"),
            """
            namespace Fixtures.Tests;

            public class GreetingFeature
            {
                [Xunit.Fact]
                [Xunit.TraitAttribute("FeatureTitle", "Greeting")]
                [Xunit.TraitAttribute("Description", "Say hello")]
                public void SayHello() { }

                [Xunit.Fact]
                [Xunit.TraitAttribute("FeatureTitle", "Greeting")]
                [Xunit.TraitAttribute("Description", "Say goodbye")]
                public void SayGoodbye() { }
            }
            """);

        TestGit.Run(Repository.Root, "add", "-A");
        TestGit.Run(Repository.Root, "commit", "-m", "a feature file and its generated class");
    }

    public void Dispose() => Repository.Dispose();
}

public sealed class FeatureFileTests(FeatureFilesFixture fixture) : IClassFixture<FeatureFilesFixture>, IDisposable
{
    private XunitFixtureRepository Repository => fixture.Repository;

    public void Dispose()
    {
        Repository.Revert();
        // A staged rename survives a checkout of the working tree; the fixture is shared across tests.
        TestGit.Run(Repository.Root, "reset", "--hard", "HEAD");
        TestGit.Run(Repository.Root, "clean", "-fd", "-q");
    }

    [Fact]
    public async Task A_changed_step_selects_the_generated_class_and_not_the_project()
    {
        Repository.Write("Fixtures.Tests/Greeting.feature", "Feature: Greeting\n  Scenario: Say hello\n    Given another name\n  Scenario: Say goodbye\n    Given a name\n");

        var report = await Repository.AnalyzeAsync();

        var selected = FixtureRepository.SelectedTests(report);
        Assert.Contains("Fixtures.Tests.GreetingFeature.SayHello", selected);
        Assert.Contains("Fixtures.Tests.GreetingFeature.SayGoodbye", selected);
        Assert.DoesNotContain("Fixtures.Tests.CalculatorTests.Adds", selected);
        Assert.Contains(report.Widenings, w => w is { Cause: "ContentFile", Scope: "Fixtures.Tests" } && w.Detail.Contains("feature file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scenario_added_since_the_last_build_widens_the_project()
    {
        // The generated class has no method for it yet, so no class selection can contain it.
        Repository.Write("Fixtures.Tests/Greeting.feature", "Feature: Greeting\n  Scenario: Say hello\n    Given a name\n  Scenario: Say goodbye\n    Given a name\n  Scenario: Say hi\n    Given a name\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Contains("Fixtures.Tests.CalculatorTests.Adds", FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task A_solution_that_reads_feature_files_widens_the_project()
    {
        Repository.Write("Fixtures.Tests/Reader.cs", "namespace Fixtures.Tests;\n\ninternal static class Reader { internal static string Text() => System.IO.File.ReadAllText(\"Greeting.feature\"); }\n");
        Repository.Write("Fixtures.Tests/Greeting.feature", "Feature: Greeting\n  Scenario: Say hello\n    Given another name\n  Scenario: Say goodbye\n    Given a name\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Contains("Fixtures.Tests.CalculatorTests.Adds", FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task A_project_file_that_ships_features_widens_the_project()
    {
        var project = Repository.PathOf("Fixtures.Tests", "Fixtures.Tests.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "<ItemGroup><None Update=\"*.feature\" CopyToOutputDirectory=\"PreserveNewest\" /></ItemGroup>\n</Project>", StringComparison.Ordinal));
        // Committed, so the project file is not itself in the diff and only the feature file is.
        TestGit.Run(Repository.Root, "commit", "-am", "ship feature files");
        try
        {
            Repository.Write("Fixtures.Tests/Greeting.feature", "Feature: Greeting\n  Scenario: Say hello\n    Given another name\n  Scenario: Say goodbye\n    Given a name\n");

            var report = await Repository.AnalyzeAsync();

            Assert.Contains("Fixtures.Tests.CalculatorTests.Adds", FixtureRepository.SelectedTests(report));
        }
        finally
        {
            // The fixture is shared by the other tests in this class.
            TestGit.Run(Repository.Root, "reset", "--hard", "HEAD~1");
        }
    }

    [Fact]
    public async Task A_renamed_feature_widens_the_project()
    {
        TestGit.Run(Repository.Root, "mv", "Fixtures.Tests/Greeting.feature", "Fixtures.Tests/Salute.feature");

        var report = await Repository.AnalyzeAsync();

        Assert.Contains("Fixtures.Tests.CalculatorTests.Adds", FixtureRepository.SelectedTests(report));
    }
}
