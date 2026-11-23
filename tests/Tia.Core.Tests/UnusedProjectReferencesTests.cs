using Microsoft.CodeAnalysis;
using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class UnusedProjectReferencesTests
{
    private static readonly Compilation Lib =
        CompilationHarness.CompileValid("public class Base { public void Hello() { } }", "Lib", "/repo/Lib.cs");

    private static Compilation WithLib(string source) =>
        CompilationHarness.Compile("App", ("/repo/App.cs", source)).AddReferences(Lib.ToMetadataReference());

    [Fact]
    public void A_reference_the_source_never_touches_is_reported()
    {
        var app = WithLib("public static class App { public static int Run() => 1; }");

        var found = UnusedProjectReferences.Find(
            [new ReferenceProject("Lib", Lib, []), new ReferenceProject("App", app, ["Lib"])]);

        Assert.Equal([new UnusedReference("App", "Lib")], found);
    }

    [Fact]
    public void A_reference_the_source_uses_is_kept()
    {
        var app = WithLib("public static class App { public static void Run() => new Base().Hello(); }");

        var found = UnusedProjectReferences.Find(
            [new ReferenceProject("Lib", Lib, []), new ReferenceProject("App", app, ["Lib"])]);

        Assert.Empty(found);
    }

    [Fact]
    public void A_reference_another_project_gets_only_through_this_one_is_kept()
    {
        // Mid uses nothing of Lib, but Top uses Base and lists only Mid: removing Mid -> Lib
        // would take Lib away from Top.
        var mid = CompilationHarness.Compile("Mid", ("/repo/Mid.cs", "public static class Mid { }"))
            .AddReferences(Lib.ToMetadataReference());
        var top = CompilationHarness.Compile("Top", ("/repo/Top.cs", "public static class Top { public static void Run() => new Base().Hello(); }"))
            .AddReferences(mid.ToMetadataReference(), Lib.ToMetadataReference());

        var found = UnusedProjectReferences.Find(
            [new ReferenceProject("Lib", Lib, []), new ReferenceProject("Mid", mid, ["Lib"]), new ReferenceProject("Top", top, ["Mid"])]);

        Assert.DoesNotContain(found, unused => unused.Project == "Mid");
    }

    [Fact]
    public void A_project_file_that_names_the_target_elsewhere_keeps_the_reference()
    {
        const string File = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\src\Polly.Core\Polly.Core.csproj" />
              </ItemGroup>
              <ItemGroup>
                <TrimmerRootAssembly Include="Polly.Core" />
              </ItemGroup>
            </Project>
            """;

        Assert.True(UnusedProjectReferences.NamedElsewhere(File, "/repo/src/Polly.Core/Polly.Core.csproj"));
    }

    [Fact]
    public void A_project_file_that_only_lists_the_reference_does_not_name_it_elsewhere()
    {
        const string File = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\src\Polly.Core\Polly.Core.csproj">
                  <PrivateAssets>all</PrivateAssets>
                </ProjectReference>
                <ProjectReference Include="..\src\Polly.Extensions\Polly.Extensions.csproj" />
              </ItemGroup>
            </Project>
            """;

        Assert.False(UnusedProjectReferences.NamedElsewhere(File, "/repo/src/Polly.Core/Polly.Core.csproj"));
    }

    [Fact]
    public void Only_a_reference_the_project_file_lists_is_the_projects_own()
    {
        const string Text = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Tests\Tests.csproj" />
              </ItemGroup>
            </Project>
            """;

        Assert.True(UnusedProjectReferences.Lists(Text, "/repo/Tests/Tests.csproj"));
        Assert.False(UnusedProjectReferences.Lists(Text, "/repo/src/Inherited/Inherited.csproj"));
    }

    [Fact]
    public void A_reference_used_by_one_framework_copy_is_kept_for_all()
    {
        var unused = WithLib("public static class App { }");
        var used = WithLib("public static class App { public static void Run() => new Base().Hello(); }");

        var found = UnusedProjectReferences.Find(
            [new ReferenceProject("Lib", Lib, []), new ReferenceProject("App", unused, ["Lib"]), new ReferenceProject("App", used, ["Lib"])]);

        Assert.Empty(found);
    }
}
