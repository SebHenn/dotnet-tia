namespace Tia.Integration.Tests;

/// <summary>
/// The fixture solution with files outside every project folder, and tests that name some of them
/// the way a test reading one by path would (#168).
/// </summary>
public sealed class UnownedFileFixture : IDisposable
{
    public XunitFixtureRepository Repository { get; } = new();

    public UnownedFileFixture()
    {
        Write("Fixtures.Tests/ReadsFilesTests.cs", """
            namespace Fixtures.Tests;

            public class ReadsFilesTests
            {
                private const string Inlined = "inlined.txt";

                private static string Schema => Path.Combine("db", "schema.sql");

                [Fact]
                public void Reads_through_a_property() => Assert.Contains("schema", Schema);

                [Fact]
                public void Reads_directly() => Assert.NotEmpty("docker-compose.yml");

                [Fact]
                public void Reads_an_interpolated_path()
                {
                    var root = "x";
                    Assert.NotEmpty($"{root}/config/settings.ini");
                }

                [Fact]
                public void Reads_a_constant() => Assert.NotEmpty(Inlined);

                [Fact]
                public void Reads_a_sample() => Assert.NotEmpty("samples/Sample.cs");

                [Theory]
                [InlineData("cases.csv")]
                public void Reads_from_an_attribute(string path) => Assert.NotEmpty(path);

                [Fact]
                public void Reads_nothing() => Assert.True(true);
            }
            """);

        Write("db/schema.sql", "create table t (id int);\n");
        Write("docker-compose.yml", "services: {}\n");
        Write("config/settings.ini", "a=1\n");
        Write("inlined.txt", "x\n");
        Write("samples/Sample.cs", "class Sample { }\n");
        Write("cases.csv", "a,b\n");
        Write("docs/guide.md", "# Guide\n");

        TestGit.Run(Repository.Root, "add", "-A");
        TestGit.Run(Repository.Root, "commit", "-m", "tests that read files outside every project");
    }

    private void Write(string relativePath, string content)
    {
        var path = Repository.PathOf(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => Repository.Dispose();
}

public sealed class UnownedFileTests(UnownedFileFixture fixture) : IClassFixture<UnownedFileFixture>, IDisposable
{
    private XunitFixtureRepository Repository => fixture.Repository;

    public void Dispose()
    {
        Repository.Revert();
        TestGit.Run(Repository.Root, "reset", "-q", "--hard");
    }

    /// <summary>The fixture's reflecting test is left out: it is selected whenever anything is.</summary>
    private static string[] Selected(Tia.Core.Reporting.AnalysisReport report) =>
        [.. XunitFixtureRepository.SelectedTests(report)
            .Select(t => t[(t.LastIndexOf('.') + 1)..])
            .Where(t => t != "Describes_a_type_it_only_knows_by_name")];

    [Fact]
    public async Task A_file_named_through_a_property_selects_the_tests_that_read_the_property()
    {
        Repository.Write("db/schema.sql", "create table t (id bigint);\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Contains("Reads_through_a_property", Selected(report));
        Assert.Contains(report.Widenings, w => w is { Cause: "ContentFile", Scope: "Fixtures.Tests" } && w.Detail.Contains("db/schema.sql", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_named_file_selects_exactly_what_changing_the_line_naming_it_would()
    {
        Repository.Write("db/schema.sql", "create table t (id bigint);\n");
        var fromTheFile = Selected(await Repository.AnalyzeAsync());
        Repository.Revert();

        Repository.Edit("Fixtures.Tests/ReadsFilesTests.cs", "\"schema.sql\"", "\"schema.txt\"");
        var fromTheLine = Selected(await Repository.AnalyzeAsync());

        Assert.Equal(fromTheLine, fromTheFile);
    }

    [Fact]
    public async Task A_file_named_inside_a_test_selects_that_test()
    {
        Repository.Write("docker-compose.yml", "services: { a: {} }\n");

        Assert.Equal(["Reads_directly"], Selected(await Repository.AnalyzeAsync()));
    }

    [Fact]
    public async Task A_file_named_in_an_interpolated_string_selects_that_test()
    {
        Repository.Write("config/settings.ini", "a=2\n");

        Assert.Equal(["Reads_an_interpolated_path"], Selected(await Repository.AnalyzeAsync()));
    }

    [Fact]
    public async Task A_file_named_by_a_constant_widens_as_a_changed_constant_does()
    {
        Repository.Write("inlined.txt", "y\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Contains("Reads_a_constant", Selected(report));
        Assert.Contains(report.Widenings, w => w is { Cause: "ConstantInlining" });
    }

    [Fact]
    public async Task A_file_named_by_a_test_attribute_selects_that_test()
    {
        Repository.Write("cases.csv", "a,c\n");

        Assert.Equal(["Reads_from_an_attribute"], Selected(await Repository.AnalyzeAsync()));
    }

    [Fact]
    public async Task A_deleted_file_is_still_named()
    {
        File.Delete(Repository.PathOf("db", "schema.sql"));

        var selected = Selected(await Repository.AnalyzeAsync());

        Assert.Contains("Reads_through_a_property", selected);
    }

    [Fact]
    public async Task A_renamed_file_is_named_by_its_old_path()
    {
        TestGit.Run(Repository.Root, "mv", "db/schema.sql", "db/renamed.sql");

        var selected = Selected(await Repository.AnalyzeAsync());

        Assert.Contains("Reads_through_a_property", selected);
    }

    [Fact]
    public async Task Csharp_no_project_compiles_is_named_like_any_other_file()
    {
        Repository.Write("samples/Sample.cs", "class Sample { int A; }\n");

        Assert.Equal(["Reads_a_sample"], Selected(await Repository.AnalyzeAsync()));
    }

    [Fact]
    public async Task A_file_nothing_names_selects_nothing_and_says_so()
    {
        Repository.Write("docs/guide.md", "# Guide, revised\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal(0, report.SelectedTests);
        Assert.Contains(report.Diagnostics, d => d.Contains("docs/guide.md", StringComparison.Ordinal) && d.Contains("in no project", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_a_project_owns_is_not_reported_as_unowned()
    {
        Repository.Write("Fixtures.Tests/data.json", "{}\n");

        var report = await Repository.AnalyzeAsync();

        Assert.DoesNotContain(report.Diagnostics, d => d.Contains("in no project", StringComparison.Ordinal));
    }
}
