namespace Tia.Integration.Tests;

/// <summary>
/// The fixture solution with files that projects include from outside their own folders, and C#
/// that no project compiles, committed so that a change to them is the whole diff (#123).
/// </summary>
public sealed class LinkedItemsFixture : IDisposable
{
    public XunitFixtureRepository Repository { get; } = new();

    public LinkedItemsFixture()
    {
        Insert("Fixtures.Tests/Fixtures.Tests.csproj", """
              <ItemGroup>
                <EmbeddedResource Include="../shared/cases.txt" />
                <None Include="../shared/data/*.json" CopyToOutputDirectory="PreserveNewest" />
                <Compile Remove="Samples/**" />
              </ItemGroup>
            """);
        Insert("Fixtures.Core/Fixtures.Core.csproj", """
              <ItemGroup>
                <None Include="../README.md" Pack="true" PackagePath="/" />
                <AdditionalFiles Include="../shared/schema.txt" />
              </ItemGroup>
            """);

        Write("shared/cases.txt", "1 2 3\n");
        Write("shared/data/first.json", "{ \"case\": 1 }\n");
        Write("shared/schema.txt", "name: string\n");
        Write("README.md", "# Fixtures\n");

        // A sample that shares a name with a real type. Read as code, a change to it was matched
        // to Fixtures.Core's Calculator.
        Write("Fixtures.Tests/Samples/Calculator.cs", "namespace Fixtures.Core;\n\npublic class Calculator { public int Add(int a, int b) => 0; }\n");

        TestGit.Run(Repository.Root, "add", "-A");
        TestGit.Run(Repository.Root, "commit", "-m", "link files from outside the projects");
    }

    /// <summary>Puts an item group into a project file without <see cref="FixtureRepository.Edit"/>,
    /// which would restore it on every revert.</summary>
    private void Insert(string project, string itemGroup)
    {
        var path = Repository.PathOf(project.Split('/'));
        File.WriteAllText(path, File.ReadAllText(path).Replace("</Project>", itemGroup + "\n</Project>", StringComparison.Ordinal));
    }

    private void Write(string relativePath, string content)
    {
        var path = Repository.PathOf(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => Repository.Dispose();
}

public sealed class LinkedItemTests(LinkedItemsFixture fixture) : IClassFixture<LinkedItemsFixture>, IDisposable
{
    private const int XunitTests = 9;

    private XunitFixtureRepository Repository => fixture.Repository;

    public void Dispose()
    {
        Repository.Revert();
        TestGit.Run(Repository.Root, "checkout", "--", ".");
    }

    [Fact]
    public async Task An_embedded_resource_outside_the_project_widens_the_project_that_embeds_it()
    {
        Repository.Write("shared/cases.txt", "1 2 3 4\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Contains(report.Widenings, w => w is { Cause: "ContentFile", Scope: "Fixtures.Tests" } && w.Detail.Contains("embeds it", StringComparison.Ordinal));
        Assert.Equal(XunitTests, report.SelectedTests);
        Assert.True(report.Timings.LinkedItemEvaluationSeconds > 0);
    }

    [Fact]
    public async Task A_deleted_file_a_glob_copied_to_the_output_widens_the_project()
    {
        File.Delete(Repository.PathOf("shared", "data", "first.json"));

        var report = await Repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w is { Cause: "ContentFile", Scope: "Fixtures.Tests" } && w.Detail.Contains("copies it", StringComparison.Ordinal));
        Assert.Equal(XunitTests, report.SelectedTests);
    }

    [Fact]
    public async Task An_additional_file_outside_the_project_widens_it_and_its_dependents()
    {
        Repository.Write("shared/schema.txt", "name: string\nage: int\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w is { Cause: "ContentFile", Scope: "Fixtures.Core" } && w.Detail.Contains("additional file", StringComparison.Ordinal));
        Assert.Contains(report.Widenings, w => w is { Cause: "Dependent", Scope: "Fixtures.Tests" });
    }

    [Fact]
    public async Task A_readme_that_is_only_packed_selects_nothing()
    {
        // Linked, but as a package's readme: it never reaches a test, and widening the library on
        // every readme edit would be a full run bought for nothing.
        Repository.Write("README.md", "# Fixtures\n\nNow with a description.\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Empty(report.Widenings);
        Assert.Equal(0, report.SelectedTests);
    }

    [Fact]
    public async Task A_change_to_csharp_no_project_compiles_widens_its_folder_project_and_matches_no_type()
    {
        Repository.Write("Fixtures.Tests/Samples/Calculator.cs", "namespace Fixtures.Core;\n\npublic class Calculator { public int Add(int a, int b) => 1; }\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w is { Cause: "ContentFile", Scope: "Fixtures.Tests" } && w.Detail.Contains("no project compiles", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Widenings, w => w is { Scope: "Fixtures.Core", Cause: not "Reflection" });
        Assert.Equal(XunitTests, report.SelectedTests);
    }

    [Fact]
    public async Task An_added_csharp_file_no_project_compiles_widens_its_folder_project()
    {
        Repository.Write("Fixtures.Tests/Samples/Added.cs", "public class Added { }\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, w => w is { Cause: "ContentFile", Scope: "Fixtures.Tests" });
        Assert.Equal(XunitTests, report.SelectedTests);
    }
}
