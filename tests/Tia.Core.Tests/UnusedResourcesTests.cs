using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class UnusedResourcesTests
{
    private static IReadOnlyList<UnusedResource> Find(string project, string source, params string[] otherSources) =>
        UnusedResources.Find(
        [
            new ResourceProject("/repo/App/App.csproj", project, [CompilationHarness.Compile("App", ("/repo/App/A.cs", source))]),
            .. otherSources.Select((text, index) =>
                new ResourceProject($"/repo/Other{index}/Other.csproj", string.Empty, [CompilationHarness.Compile($"Other{index}", ($"/repo/Other{index}/B.cs", text))])),
        ]);

    private static string Items(string element) => $"<Project><ItemGroup>{element}</ItemGroup></Project>";

    [Fact]
    public void A_resource_no_source_names_is_reported()
    {
        var found = Find(Items("<EmbeddedResource Include=\"Data/old.json\" />"), "class A { }");

        Assert.Equal([new UnusedResource("/repo/App/App.csproj", "Data/old.json")], found);
    }

    [Fact]
    public void A_resource_whose_file_name_ends_a_string_literal_is_kept()
    {
        var found = Find(Items("<EmbeddedResource Include=\"Data/old.json\" />"), "class A { string N = \"App.Data.old.json\"; }");

        Assert.Empty(found);
    }

    [Fact]
    public void A_resource_another_project_reads_is_kept()
    {
        var found = Find(Items("<EmbeddedResource Include=\"old.json\" />"), "class A { }", "class B { string N = \"App.old.json\"; }");

        Assert.Empty(found);
    }

    [Fact]
    public void A_project_that_enumerates_its_resources_is_not_judged()
    {
        var found = Find(Items("<EmbeddedResource Include=\"old.json\" />"), "class A { object N = typeof(A).Assembly.GetManifestResourceNames(); }");

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("<EmbeddedResource Include=\"Strings.resx\" />")]
    [InlineData("<EmbeddedResource Include=\"Data/*.json\" />")]
    [InlineData("<EmbeddedResource Include=\"old.json\" Condition=\"'$(X)' == 'y'\" />")]
    public void An_item_that_is_not_listed_plainly_is_not_judged(string element)
    {
        Assert.Empty(Find(Items(element), "class A { }"));
    }

    [Fact]
    public void A_logical_name_is_what_the_source_must_name()
    {
        var found = Find(Items("<EmbeddedResource Include=\"old.json\" LogicalName=\"cfg.json\" />"), "class A { string N = \"old.json\"; }");

        Assert.Single(found);
    }

    [Fact]
    public void Each_file_in_a_semicolon_list_is_judged_on_its_own()
    {
        var found = Find(Items("<EmbeddedResource Include=\"a/used.conf;b/unused.conf\" />"), "class A { string N = \"App.used.conf\"; }");

        Assert.Equal([new UnusedResource("/repo/App/App.csproj", "b/unused.conf")], found);
    }
}
