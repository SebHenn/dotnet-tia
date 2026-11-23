using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class OrphanedFilesTests
{
    private const string Plain = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private static IReadOnlyList<string> Find(
        string[] sources, string[] compiled, string project = Plain, string[]? loaded = null, params BuildFile[] more) =>
        OrphanedFiles.Find(new OrphanInput(
            sources,
            new HashSet<string>(compiled, StringComparer.Ordinal),
            new HashSet<string>(loaded ?? ["/repo/App/App.csproj"], StringComparer.Ordinal),
            [new BuildFile("/repo/App/App.csproj", project), .. more]));

    [Fact]
    public void A_file_in_a_loaded_projects_folder_that_nothing_compiles_is_reported()
    {
        var found = Find(["/repo/App/A.cs", "/repo/App/Old.cs"], ["/repo/App/A.cs"]);

        Assert.Equal(["/repo/App/Old.cs"], found);
    }

    [Fact]
    public void A_file_under_a_project_that_did_not_load_is_left_alone()
    {
        var found = Find(["/repo/App/Old.cs"], [], loaded: []);

        Assert.Empty(found);
    }

    [Fact]
    public void A_file_under_no_project_is_reported()
    {
        var found = Find(["/repo/scripts/Tool.cs"], []);

        Assert.Equal(["/repo/scripts/Tool.cs"], found);
    }

    [Theory]
    [InlineData("<Project><ItemGroup><Compile Include=\"..\\scripts\\*.cs\" /></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup><Compile Include=\"..\\scripts\\Other.cs\" Link=\"Other.cs\" /></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup><EmbeddedResource Include=\"**\\*.cs\" /></ItemGroup></Project>")]
    public void A_file_under_no_project_is_kept_when_any_project_could_pull_files_in_from_outside(string project)
    {
        Assert.Empty(Find(["/repo/scripts/Tool.cs"], [], project));
    }


    [Fact]
    public void A_project_that_removes_or_globs_compile_items_is_not_judged()
    {
        var removing = "<Project><ItemGroup><Compile Remove=\"Old/**\" /></ItemGroup></Project>";

        Assert.Empty(Find(["/repo/App/Old/X.cs"], [], removing));
    }

    [Fact]
    public void A_compile_item_with_a_condition_is_not_judged()
    {
        var conditional = "<Project><ItemGroup><Compile Include=\"X.cs\" Condition=\"'$(Legacy)' == 'true'\" /></ItemGroup></Project>";

        Assert.Empty(Find(["/repo/App/Other.cs"], [], conditional));
    }

    [Fact]
    public void A_file_a_build_file_names_is_left_alone()
    {
        var props = new BuildFile("/repo/Directory.Build.targets", "<Project><Target Name=\"T\"><Message Text=\"Generated.cs\" /></Target></Project>");

        Assert.Empty(Find(["/repo/App/Generated.cs"], [], Plain, null, props));
    }

    [Fact]
    public void A_props_file_above_the_project_that_shapes_compile_items_stops_the_report()
    {
        var props = new BuildFile("/repo/Directory.Build.props", "<Project><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>");

        Assert.Empty(Find(["/repo/App/Old.cs"], [], Plain, null, props));
    }

    [Fact]
    public void A_props_file_in_an_unrelated_folder_does_not_stop_it()
    {
        var props = new BuildFile("/repo/Other/Directory.Build.props", "<Project><ItemGroup><Compile Remove=\"x\" /></ItemGroup></Project>");

        Assert.Equal(["/repo/App/Old.cs"], Find(["/repo/App/Old.cs"], [], Plain, null, props));
    }
}
