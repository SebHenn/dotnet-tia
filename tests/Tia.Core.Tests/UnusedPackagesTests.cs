using System.Text.Json;
using Microsoft.CodeAnalysis;
using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class UnusedPackagesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tia-packages-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_package_whose_assembly_the_source_never_touches_is_reported()
    {
        var assets = Restore(("pkga", "class PkgA { }", []));
        var app = Compile(assets, "pkga", "public static class App { }");

        var found = UnusedPackages.Find([new PackageProject("App", [app], assets)]);

        Assert.Equal([new UnusedPackage("App", "pkga")], found);
    }

    [Fact]
    public void A_package_the_source_uses_is_kept()
    {
        var assets = Restore(("pkga", "public class PkgA { public static int One => 1; }", []));
        var app = Compile(assets, "pkga", "public static class App { public static int Run() => PkgA.One; }");

        Assert.Empty(UnusedPackages.Find([new PackageProject("App", [app], assets)]));
    }

    [Fact]
    public void A_package_is_kept_when_only_a_package_it_alone_brings_is_used()
    {
        // The source names PkgB, which the project does not list: it arrives through PkgA.
        var assets = Restore(
            ("pkga", "public class PkgA { }", ["pkgb"]),
            ("pkgb", "public class PkgB { public static int One => 1; }", []));
        var app = Compile(assets, "pkga", "public static class App { public static int Run() => PkgB.One; }", "pkgb");

        Assert.Empty(UnusedPackages.Find([new PackageProject("App", [app], assets)]));
    }

    [Fact]
    public void A_package_with_no_assembly_for_the_compiler_is_not_judged()
    {
        var assets = Restore(("analyzers", null, []));
        var app = CompilationHarness.Compile("App", ("/repo/App.cs", "public static class App { }"));

        Assert.Empty(UnusedPackages.Find([new PackageProject("App", [app], assets)]));
    }

    [Fact]
    public void A_package_used_by_one_framework_copy_is_kept_for_all()
    {
        var assets = Restore(("pkga", "public class PkgA { public static int One => 1; }", []));
        var unused = Compile(assets, "pkga", "public static class App { }");
        var used = Compile(assets, "pkga", "public static class App { public static int Run() => PkgA.One; }");

        Assert.Empty(UnusedPackages.Find([new PackageProject("App", [unused, used], assets)]));
    }

    [Fact]
    public void The_assets_file_lists_direct_packages_and_what_each_contributes()
    {
        var assets = Restore(
            ("pkga", "public class PkgA { }", ["pkgb"]),
            ("pkgb", "public class PkgB { }", []));

        Assert.Equal(["pkga"], assets.Direct);
        Assert.Equal(["pkgb"], assets.Dependencies["pkga"]);
        Assert.Single(assets.Assemblies["pkgb"]);
    }

    private Microsoft.CodeAnalysis.CSharp.CSharpCompilation Compile(PackageAssets assets, string direct, string source, params string[] alsoReferenced)
    {
        var references = new[] { direct }.Concat(alsoReferenced)
            .SelectMany(id => assets.Assemblies[id])
            .Select(path => MetadataReference.CreateFromFile(path));
        return CompilationHarness.Compile("App", ("/repo/App.cs", source)).AddReferences(references);
    }

    private PackageAssets Restore(params (string Id, string? Source, string[] DependsOn)[] packages)
    {
        var libraries = new Dictionary<string, object>();
        var target = new Dictionary<string, object>();
        foreach (var (id, source, dependsOn) in packages)
        {
            var key = id + "/1.0.0";
            libraries[key] = new { type = "package", path = id + "/1.0.0" };
            var compile = new Dictionary<string, object>();
            if (source is not null)
            {
                var dll = Path.Combine(folder, id, "1.0.0", "lib", "net10.0", id + ".dll");
                Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
                var emitted = CompilationHarness.Compile(id, ("/pkg/" + id + ".cs", source)).Emit(dll);
                Assert.True(emitted.Success);
                compile["lib/net10.0/" + id + ".dll"] = new { };
            }

            target[key] = new
            {
                type = "package",
                dependencies = dependsOn.ToDictionary(dependency => dependency, _ => "1.0.0"),
                compile,
            };
        }

        // Only the first package is listed by the project; the rest arrive through it.
        var json = JsonSerializer.Serialize(new
        {
            version = 3,
            targets = new Dictionary<string, object> { ["net10.0"] = target },
            libraries,
            projectFileDependencyGroups = new Dictionary<string, string[]> { ["net10.0"] = [packages[0].Id + " >= 1.0.0"] },
            packageFolders = new Dictionary<string, object> { [folder + Path.DirectorySeparatorChar] = new { } },
        });
        return PackageAssets.Parse(json);
    }
}
