using Microsoft.CodeAnalysis;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// A workspace kept open across edits, and whether it still answers the same question.
/// </summary>
/// <remarks>
/// Everything else about <c>watch</c> is a performance argument. This is the correctness one: a
/// resident workspace that refreshes wrongly does not fail, it selects too little. A document left
/// holding yesterday's text keeps its project's content hash matching, so the cached fragment
/// stays valid, so the change that was actually made seeds nothing at all - a miss reported as a
/// clean selective run.
/// </remarks>
[Collection(nameof(FixtureCollection))]
public sealed class ResidentWorkspaceTests(XunitFixtureRepository repository) : IDisposable
{
    private const string Original = "public int Add(int a, int b) => a + b;";

    private const string Edited = "public int Add(int a, int b) => b + a;";

    public void Dispose() => repository.Revert();

    [Fact]
    public async Task A_refreshed_workspace_selects_what_a_freshly_loaded_one_selects()
    {
        // The reference answer: one process, one load, one analysis - the shape every other
        // command uses.
        repository.Edit("Fixtures.Core/Calculator.cs", Original, Edited);
        var fresh = await repository.AnalyzeAsync();
        repository.Revert();

        Assert.True(fresh.SelectedTests > 0, "the edit should select something for this to compare");

        using var resident = await ResidentWorkspace.OpenAsync(
            repository.SolutionPath, repository.AnalysisRoot, cancellationToken: TestContext.Current.CancellationToken);

        var clean = await AnalyseAsync(resident);
        Assert.Equal(0, clean.SelectedTests);

        repository.Edit("Fixtures.Core/Calculator.cs", Original, Edited);
        var refreshed = await AnalyseAsync(resident);

        Assert.Equal(fresh.SelectedTests, refreshed.SelectedTests);
        Assert.Equal(Selected(fresh), Selected(refreshed));
    }

    [Fact]
    public async Task A_file_rewritten_with_its_own_content_rebinds_nothing()
    {
        using var resident = await ResidentWorkspace.OpenAsync(
            repository.SolutionPath, repository.AnalysisRoot, cancellationToken: TestContext.Current.CancellationToken);

        var path = repository.PathOf("Fixtures.Core", "Calculator.cs");
        File.WriteAllText(path, File.ReadAllText(path));

        // A new write time and identical bytes. Deciding on the timestamp would rebind here and
        // re-parse a project for a change nobody made; deciding on the content is why the refresh
        // reads rather than stats.
        Assert.Equal(0, resident.Refresh(cancellationToken: TestContext.Current.CancellationToken).ChangedFiles);
    }

    /// <summary>
    /// The bug this keying was changed for. A multi-targeted project arrives as one Roslyn project
    /// per framework, all sharing one project file, so a descriptor cache keyed by file path handed
    /// every framework the last one's descriptor. Two contexts then called themselves
    /// <c>Tia.Cli(net10.0)</c>, and the first dictionary downstream threw on the duplicate - which
    /// the error fallback turned into a silent full run on every edit after the first.
    /// </summary>
    [Fact]
    public void Each_framework_of_a_multi_targeted_project_keeps_its_own_descriptor()
    {
        using var adhoc = new AdhocWorkspace();

        var file = Path.Combine(repository.AnalysisRoot, "Fixtures.Core", "Fixtures.Core.csproj");
        var nine = Add(adhoc, "Shared(net9.0)", file);
        var ten = Add(adhoc, "Shared(net10.0)", file);

        var descriptors = new Dictionary<ProjectId, ProjectDescriptor>
        {
            [nine.Id] = Descriptor("Shared(net9.0)", file),
            [ten.Id] = Descriptor("Shared(net10.0)", file),
        };

        var bound = WorkspaceLoader.Bind(
            adhoc.CurrentSolution, repository.AnalysisRoot, [], descriptors, null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Shared(net10.0)", "Shared(net9.0)"],
            bound.Projects.Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("src/App/App.csproj", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("Directory.Build.targets", true)]
    [InlineData("tia.slnx", true)]
    [InlineData("src/App/Program.cs", false)]
    [InlineData("README.md", false)]
    public void Only_the_files_that_move_MSBuilds_own_globs_force_a_reload(string path, bool expected)
    {
        // A refresh replaces the text of documents the solution already holds. Which documents it
        // holds is MSBuild's answer, not one this tool may improvise, so anything that could change
        // that answer pays the load again instead.
        Assert.Equal(expected, ResidentWorkspace.NeedsReopen(path));
    }

    private static Project Add(AdhocWorkspace workspace, string name, string filePath) =>
        workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name,
            assemblyName: "Shared",
            language: LanguageNames.CSharp,
            filePath: filePath));

    private static ProjectDescriptor Descriptor(string name, string filePath) => new()
    {
        Name = name,
        AssemblyName = "Shared",
        FilePath = filePath,
        ProjectReferences = [],
    };

    private static IReadOnlyList<string> Selected(AnalysisReport report) =>
        [.. report.Projects.Select(p => $"{p.Name}:{p.SelectedTests}").Order(StringComparer.Ordinal)];

    private async Task<AnalysisReport> AnalyseAsync(ResidentWorkspace resident)
    {
        var outcome = await new SolutionAnalyzer(
            new AnalysisOptions
            {
                RepositoryRoot = repository.AnalysisRoot,
                BaseRef = "HEAD",
                SolutionPath = repository.SolutionPath,
                UseCache = false,
                FallbackFullOnError = false,
            },
            (clock, token) => Task.FromResult(resident.Refresh(clock, token).Workspace))
            .AnalyzeAsync(TestContext.Current.CancellationToken);

        return outcome.Report;
    }
}
