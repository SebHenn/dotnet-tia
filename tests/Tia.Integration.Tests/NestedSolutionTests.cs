namespace Tia.Integration.Tests;

/// <summary>
/// A solution that lives below the git root, which is what a monorepo looks like. Diff paths come
/// from git and are relative to the repository, not to the directory being analysed; resolving
/// them against the wrong one matched no document, so every analysis selected nothing and said so
/// without complaint.
/// </summary>
[Collection(nameof(NestedFixtureCollection))]
public sealed class NestedSolutionTests(NestedFixtureRepository repository) : IDisposable
{
    public void Dispose() => repository.Revert();

    [Fact]
    public async Task A_solution_below_the_git_root_still_selects()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");

        var report = await repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal(
            [
                "Fixtures.Tests.CalculatorTests.Adds",
                "Fixtures.Tests.ReflectiveFactoryTests.Describes_a_type_it_only_knows_by_name",
            ],
            FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task An_untouched_nested_solution_selects_nothing()
    {
        var report = await repository.AnalyzeAsync();

        Assert.Equal(0, report.SelectedTests);
        Assert.True(report.TotalTests > 0, "no tests were discovered, so selecting none proves nothing");
    }
}

[CollectionDefinition(nameof(NestedFixtureCollection))]
public sealed class NestedFixtureCollection : ICollectionFixture<NestedFixtureRepository>;
