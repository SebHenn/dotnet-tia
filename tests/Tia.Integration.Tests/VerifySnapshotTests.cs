namespace Tia.Integration.Tests;

/// <summary>
/// The fixture solution with a project that references an assembly named like Verify's, and
/// snapshots beside its tests, committed so that a change to a snapshot is the whole diff (#172).
/// The reference is a copy of an unrelated assembly under Verify's name: what the engine reads is
/// the file name of the reference, not what is in it.
/// </summary>
public sealed class VerifySnapshotsFixture : IDisposable
{
    public XunitFixtureRepository Repository { get; } = new();

    public VerifySnapshotsFixture()
    {
        var stub = Repository.PathOf("libs", "Verify.Stub.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(stub)!);
        File.Copy(typeof(XunitFixtureRepository).Assembly.Location, stub);

        var project = Repository.PathOf("Fixtures.Tests", "Fixtures.Tests.csproj");
        File.WriteAllText(
            project,
            File.ReadAllText(project).Replace(
                "</Project>",
                "<ItemGroup><Reference Include=\"Verify.Stub\"><HintPath>../libs/Verify.Stub.dll</HintPath></Reference></ItemGroup>\n</Project>",
                StringComparison.Ordinal));

        File.WriteAllText(Repository.PathOf("Fixtures.Tests", "CalculatorTests.Adds.verified.txt"), "3\n");
        File.WriteAllText(Repository.PathOf("Fixtures.Tests", "Orphan.Name.verified.txt"), "x\n");

        TestGit.Run(Repository.Root, "add", "-A");
        TestGit.Run(Repository.Root, "commit", "-m", "snapshots beside the tests");
    }

    public void Dispose() => Repository.Dispose();
}

public sealed class VerifySnapshotTests(VerifySnapshotsFixture fixture) : IClassFixture<VerifySnapshotsFixture>, IDisposable
{
    private const int XunitTests = 9;

    private XunitFixtureRepository Repository => fixture.Repository;

    public void Dispose()
    {
        Repository.Revert();
        TestGit.Run(Repository.Root, "checkout", "--", ".");
        TestGit.Run(Repository.Root, "clean", "-fd", "-q");
    }

    [Fact]
    public async Task A_changed_snapshot_selects_the_test_it_is_named_for()
    {
        Repository.Write("Fixtures.Tests/CalculatorTests.Adds.verified.txt", "4\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        // The reflective test is selected by any change to a type in its assembly, whatever the cause.
        var selected = FixtureRepository.SelectedTests(report);
        Assert.Equal(2, selected.Count);
        Assert.Contains("Fixtures.Tests.CalculatorTests.Adds", selected);
        Assert.Contains(selected, t => t.StartsWith("Fixtures.Tests.ReflectiveFactoryTests.", StringComparison.Ordinal));
        Assert.Contains(report.Widenings, w => w is { Cause: "ContentFile", Scope: "Fixtures.Tests" } && w.Detail.Contains("Verify snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_snapshot_that_names_no_test_widens_the_project()
    {
        Repository.Write("Fixtures.Tests/Orphan.Name.verified.txt", "y\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal(XunitTests, report.SelectedTests);
    }

    [Fact]
    public async Task A_solution_that_renames_snapshots_widens_the_project()
    {
        // Any test can send its snapshot anywhere with UseFileName, so no name can be trusted.
        Repository.Write("Fixtures.Tests/Naming.cs", "namespace Fixtures.Tests;\n\ninternal static class Naming { internal const string Use = \"UseFileName\"; }\n");
        Repository.Write("Fixtures.Tests/CalculatorTests.Adds.verified.txt", "4\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal(XunitTests, report.SelectedTests);
    }
}
