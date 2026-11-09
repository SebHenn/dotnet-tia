namespace Tia.Integration.Tests;

/// <summary>
/// Razor Pages end to end, against the Razor generator of the SDK the suite runs on (#169, #182).
/// </summary>
/// <remarks>
/// <para>
/// Three things have to hold at once for these to select one test: the generator loads into the
/// Roslyn this tool carries, a page's route joins the URL its test requests, and re-running the
/// generator over both revisions shows a C# change left the generated page alone. The last needs
/// the project's additional files and analyzer config, which is where the <c>.cshtml</c> and the
/// generator's settings come from; without them every C# change selected every page test.
/// </para>
/// <para>
/// If these fail with every test selected and a <c>SourceGenerator</c> widening naming
/// <c>Microsoft.CodeAnalysis.Razor.Compiler.dll</c>, the SDK has moved to a Razor generator built
/// against a newer Roslyn than the one in <c>RoslynVersion</c>, in <c>Directory.Packages.props</c>.
/// Raise it there.
/// </para>
/// </remarks>
public sealed class RazorPageSelectionTests(WebFixtureRepositoryFixture fixture) : IClassFixture<WebFixtureRepositoryFixture>, IDisposable
{
    private WebFixtureRepository Repository => fixture.Repository;

    public void Dispose()
    {
        Repository.Revert();
        TestGit.Run(Repository.Root, "reset", "-q", "--hard");
    }

    private async Task<IReadOnlyList<string>> SelectedAfter(string file, string find, string replace)
    {
        Repository.Edit(file, find, replace);

        var report = await Repository.AnalyzeAsync();

        Assert.DoesNotContain(report.Widenings, w => w.Detail.Contains("could not be loaded", StringComparison.Ordinal));
        return FixtureRepository.SelectedTests(report);
    }

    [Fact]
    public async Task A_handler_change_selects_the_test_requesting_its_page()
    {
        var selected = await SelectedAfter("Web.Api/Pages/Contributors/Show.cshtml.cs",
            "Heading = \"Contributor \" + id;", "Heading = \"Contributor  \" + id;");

        Assert.Equal(["Web.Tests.EndpointTests.Shows_a_contributor_page"], selected);
    }

    [Fact]
    public async Task A_getter_only_the_page_reads_selects_the_test_requesting_the_page()
    {
        var selected = await SelectedAfter("Web.Api/Pages/Contributors/Show.cshtml.cs",
            "$\"contributor #{Id}\"", "$\"contributor no {Id}\"");

        Assert.Equal(["Web.Tests.EndpointTests.Shows_a_contributor_page"], selected);
    }

    [Fact]
    public async Task A_page_model_constructor_selects_the_test_requesting_its_page()
    {
        var selected = await SelectedAfter("Web.Api/Pages/About.cshtml.cs",
            "\"about contributors\"", "\"about\"");

        Assert.Equal(["Web.Tests.EndpointTests.Shows_the_about_page"], selected);
    }

    /// <summary>
    /// The project has Razor files, so its generated pages are re-run for every C# change in it.
    /// When they come out the same, an endpoint change selects only its own test.
    /// </summary>
    [Fact]
    public async Task An_endpoint_change_beside_the_pages_does_not_select_them()
    {
        var selected = await SelectedAfter("Web.Api/Contributors.cs",
            "new Contributor(id, \"ada\")", "new Contributor(id, \"bob\")");

        Assert.Equal(["Web.Tests.EndpointTests.Gets_a_contributor_by_id"], selected);
    }
}

/// <summary>One copy of the web fixture, restored once, for the tests above.</summary>
public sealed class WebFixtureRepositoryFixture : IDisposable
{
    public WebFixtureRepository Repository { get; } = new();

    public void Dispose() => Repository.Dispose();
}
