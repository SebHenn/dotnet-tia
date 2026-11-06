namespace Tia.Integration.Tests;

/// <summary>
/// The fixture solution with a central package file. Its own settings leave central package
/// management off, so the entries here change no restore: what is under test is how a change to
/// them is read, against the assets files the fixture was already restored with (#171).
/// </summary>
public sealed class PackageBumpFixture : IDisposable
{
    public XunitFixtureRepository Repository { get; } = new();

    public PackageBumpFixture()
    {
        File.WriteAllText(Repository.PathOf("Directory.Packages.props"), """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="NUnit" Version="1.0.0" />
                <PackageVersion Include="xunit.v3" Version="1.0.0" />
                <PackageVersion Include="xunit.v3.assert" Version="1.0.0" />
                <PackageVersion Include="Nothing.Uses.This" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        // A library with a package of its own, which the fixture does not need to have restored:
        // reading a bump of it asks nothing of the package.
        var core = Repository.PathOf("Fixtures.Core", "Fixtures.Core.csproj");
        File.WriteAllText(core, File.ReadAllText(core).Replace(
            "</Project>",
            "  <ItemGroup>\n    <PackageReference Include=\"Not.Restored\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>",
            StringComparison.Ordinal));

        TestGit.Run(Repository.Root, "add", "-A");
        TestGit.Run(Repository.Root, "commit", "-m", "a central package file");
    }

    public void Dispose() => Repository.Dispose();
}

public sealed class PackageBumpTests(PackageBumpFixture fixture) : IClassFixture<PackageBumpFixture>, IDisposable
{
    private const string Central = "Directory.Packages.props";

    private XunitFixtureRepository Repository => fixture.Repository;

    public void Dispose()
    {
        Repository.Revert();
        TestGit.Run(Repository.Root, "reset", "-q", "--hard");
    }

    /// <summary>
    /// Every test project is listed whatever was selected from it, so the question is what was. The
    /// fixture's reflecting test is left out: it is selected whenever anything is.
    /// </summary>
    private static IReadOnlyList<string> Tests(Tia.Core.Reporting.AnalysisReport report, string project) =>
        [.. report.Projects.Single(p => p.Name == project).Tests.Where(t => !t.Contains("ReflectiveFactoryTests", StringComparison.Ordinal))];

    [Fact]
    public async Task A_central_bump_widens_the_projects_resolving_that_package()
    {
        Repository.Edit(Central, "Include=\"NUnit\" Version=\"1.0.0\"", "Include=\"NUnit\" Version=\"1.0.1\"");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Contains(report.Widenings, w => w is { Cause: "PackageVersion", Scope: "Fixtures.NUnitTests" } && w.Detail.Contains("NUnit", StringComparison.Ordinal));
        Assert.NotEmpty(Tests(report, "Fixtures.NUnitTests"));
        Assert.Empty(Tests(report, "Fixtures.Tests"));
    }

    [Fact]
    public async Task A_transitive_package_counts_as_resolved()
    {
        // Fixtures.Tests names xunit.v3, which brings xunit.v3.assert in without naming it.
        Repository.Edit(Central, "Include=\"xunit.v3.assert\" Version=\"1.0.0\"", "Include=\"xunit.v3.assert\" Version=\"1.0.1\"");

        var report = await Repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w is { Cause: "PackageVersion", Scope: "Fixtures.Tests" });
        Assert.Contains(Tests(report, "Fixtures.Tests"), t => t.EndsWith("CalculatorTests.Adds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_package_nothing_resolves_selects_nothing_and_says_so()
    {
        Repository.Edit(Central, "Include=\"Nothing.Uses.This\" Version=\"1.0.0\"", "Include=\"Nothing.Uses.This\" Version=\"1.0.1\"");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal(0, report.SelectedTests);
        Assert.Contains(report.Diagnostics, d => d.Contains("Nothing.Uses.This", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_comment_in_the_central_file_selects_nothing_and_says_so()
    {
        Repository.Edit(Central, "<ItemGroup>", "<ItemGroup><!-- pinned -->");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal(0, report.SelectedTests);
        Assert.Contains(report.Diagnostics, d => d.Contains(Central, StringComparison.Ordinal) && d.Contains("no package version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anything_else_in_the_central_file_is_still_a_full_run()
    {
        Repository.Edit(Central, "<ManagePackageVersionsCentrally>false", "<ManagePackageVersionsCentrally>False");

        var report = await Repository.AnalyzeAsync();

        Assert.True(report.IsFullRun);
        Assert.Contains(report.FullRunReasons, r => r.Contains(Central, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_file_bump_widens_that_project_and_not_its_siblings()
    {
        var project = File.ReadAllText(Repository.PathOf("Fixtures.NUnitTests", "Fixtures.NUnitTests.csproj"));
        var version = System.Text.RegularExpressions.Regex.Match(project, "Include=\"NUnit\" Version=\"([^\"]+)\"").Groups[1].Value;

        Repository.Edit(
            "Fixtures.NUnitTests/Fixtures.NUnitTests.csproj",
            $"Include=\"NUnit\" Version=\"{version}\"",
            $"Include=\"NUnit\" Version=\"{version}.1\"");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Contains(report.Widenings, w => w is { Cause: "PackageVersion", Scope: "Fixtures.NUnitTests" });
        Assert.Empty(Tests(report, "Fixtures.Tests"));
    }

    [Fact]
    public async Task A_bump_in_a_library_reaches_the_projects_referencing_it()
    {
        Repository.Edit("Fixtures.Core/Fixtures.Core.csproj", "Include=\"Not.Restored\" Version=\"1.0.0\"", "Include=\"Not.Restored\" Version=\"1.0.1\"");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Contains(report.Widenings, w => w is { Cause: "PackageVersion", Scope: "Fixtures.Core" });
        Assert.Contains(Tests(report, "Fixtures.Tests"), t => t.EndsWith("CalculatorTests.Adds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_comment_in_a_project_file_selects_nothing()
    {
        Repository.Edit("Fixtures.Core/Fixtures.Core.csproj", "<ItemGroup>", "<!-- a note --><ItemGroup>");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal(0, report.SelectedTests);
    }

    [Fact]
    public async Task Anything_else_in_a_project_file_is_still_a_full_run()
    {
        Repository.Edit("Fixtures.Core/Fixtures.Core.csproj", "<RootNamespace>Fixtures.Core</RootNamespace>", "<RootNamespace>Fixtures.Other</RootNamespace>");

        Assert.True((await Repository.AnalyzeAsync()).IsFullRun);
    }

    [Fact]
    public async Task A_project_whose_packages_cannot_be_read_forces_a_full_run()
    {
        var assets = Repository.PathOf("Fixtures.Core", "obj", "project.assets.json");
        File.Move(assets, assets + ".moved");

        try
        {
            Repository.Edit(Central, "Include=\"NUnit\" Version=\"1.0.0\"", "Include=\"NUnit\" Version=\"1.0.1\"");

            var report = await Repository.AnalyzeAsync();

            Assert.True(report.IsFullRun);
            Assert.Contains(report.FullRunReasons, r => r.Contains("Fixtures.Core", StringComparison.Ordinal) && r.Contains("project.assets.json", StringComparison.Ordinal));
        }
        finally
        {
            File.Move(assets + ".moved", assets);
        }
    }

    [Fact]
    public async Task A_bump_alongside_another_change_keeps_both()
    {
        Repository.Edit(Central, "Include=\"NUnit\" Version=\"1.0.0\"", "Include=\"NUnit\" Version=\"1.0.1\"");
        Repository.Edit("Fixtures.Core/Calculator.cs", "a + b", "b + a");

        var report = await Repository.AnalyzeAsync();

        Assert.NotEmpty(Tests(report, "Fixtures.NUnitTests"));
        Assert.Contains(XunitFixtureRepository.SelectedTests(report), t => t.EndsWith("CalculatorTests.Adds", StringComparison.Ordinal));
    }
}
