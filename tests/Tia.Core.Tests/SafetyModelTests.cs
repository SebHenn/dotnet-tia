using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Safety;
using Tia.Frameworks;

namespace Tia.Core.Tests;

public sealed class SafetyModelTests
{
    [Theory]
    [InlineData("src/App/App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    [InlineData("global.json")]
    [InlineData("NuGet.config")]
    [InlineData(".editorconfig")]
    [InlineData("src/packages.lock.json")]
    [InlineData("tia.sln")]
    [InlineData("build/Common.targets")]
    public void Build_inputs_force_a_full_run(string path)
    {
        Assert.NotNull(FullRunTriggers.Match(path));
    }

    [Theory]
    [InlineData("src/App/Widget.cs")]
    [InlineData("README.md")]
    [InlineData("tests/data/sample.json")]
    public void Ordinary_files_do_not(string path)
    {
        Assert.Null(FullRunTriggers.Match(path));
    }

    [Fact]
    public void The_reason_names_the_file_that_triggered_it()
    {
        var reasons = FullRunTriggers.Scan([
            new ChangedFile { Path = "src/App/App.csproj", Kind = FileChangeKind.Modified },
            new ChangedFile { Path = "src/App/Widget.cs", Kind = FileChangeKind.Modified },
        ]);

        Assert.Contains("src/App/App.csproj", Assert.Single(reasons));
    }

    [Theory]
    [InlineData("tests/data/fixture.json", true)]
    [InlineData("src/App/Strings.resx", true)]
    [InlineData("tests/data/seed.sql", true)]
    [InlineData("src/App/Widget.cs", false)]
    public void Non_source_content_widens_rather_than_bailing(string path, bool widens)
    {
        Assert.Equal(widens, ContentFileRules.IsWideningContent(path));
    }

    [Theory]
    [InlineData("var t = System.Type.GetType(name);")]
    [InlineData("var o = System.Activator.CreateInstance(t);")]
    [InlineData("var m = t.GetMethod(\"Run\"); m.Invoke(null, null);")]
    [InlineData("dynamic d = value;")]
    public void Reflection_is_detected(string statement)
    {
        var tree = CSharpSyntaxTree.ParseText($$"""
            using System;
            using System.Reflection;
            public class Probe
            {
                public void Run(string name, Type t, object value) { {{statement}} }
            }
            """);

        Assert.NotEmpty(ReflectionScanner.Scan(tree));
    }

    [Fact]
    public void Ordinary_code_is_not_flagged_as_reflection()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            public class Probe
            {
                public int Run(int a, int b) => a + b;
            }
            """);

        Assert.Empty(ReflectionScanner.Scan(tree));
    }

    [Fact]
    public void A_deleted_member_widens_to_its_declaring_type()
    {
        // The new tree has no trace of Removed(), and every caller still compiles, so nothing but
        // the old side can surface this change.
        const string current = """
            namespace App
            {
                public class Widget
                {
                    public int Kept() => 1;
                }
            }
            """;

        const string atBase = """
            namespace App
            {
                public class Widget
                {
                    public int Kept() => 1;
                    public int Removed() => 2;
                }
            }
            """;

        var compilation = CompilationHarness.Compile(current);
        var index = SourceTypeIndex.Build(compilation);

        var changed = new OldSideResolver(index).Resolve(atBase, [new LineRange(6, 6)], "App", "Widget.cs");

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Widget"), changed.Keys);
    }

    [Fact]
    public void A_deleted_type_widens_to_the_whole_project()
    {
        const string current = "namespace App { public class Kept { } }";
        const string atBase = """
            namespace App
            {
                public class Kept { }
                public class Removed { public int Value() => 1; }
            }
            """;

        var index = SourceTypeIndex.Build(CompilationHarness.Compile(current));

        var changed = new OldSideResolver(index).Resolve(atBase, [new LineRange(4, 4)], "App", "Removed.cs");

        Assert.Contains(changed.ProjectWide, c => c.Cause == ProjectWideCause.DeletedType);
    }

    [Fact]
    public void A_removed_override_is_found_through_the_old_side()
    {
        // Deleting an override silently redirects dispatch to the base implementation. Nothing in
        // the new tree marks it, and every caller still compiles.
        const string current = """
            namespace App
            {
                public class Base { public virtual int Value() => 1; }
                public class Derived : Base { }
            }
            """;

        const string atBase = """
            namespace App
            {
                public class Base { public virtual int Value() => 1; }
                public class Derived : Base { public override int Value() => 2; }
            }
            """;

        var compilation = CompilationHarness.Compile(current);
        var index = SourceTypeIndex.Build(compilation);

        var changed = new OldSideResolver(index).Resolve(atBase, [new LineRange(4, 4)], "App", "Derived.cs");

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Derived"), changed.Keys);
    }

    [Theory]
    [InlineData("xunit.v3.core", TestFramework.XUnitV3)]
    [InlineData("xunit.core", TestFramework.XUnitV2)]
    [InlineData("nunit.framework", TestFramework.NUnit)]
    [InlineData("Microsoft.VisualStudio.TestPlatform.TestFramework", TestFramework.MSTest)]
    [InlineData("TUnit.Core", TestFramework.TUnit)]
    [InlineData("Newtonsoft.Json", TestFramework.Unknown)]
    public void Frameworks_are_detected_from_referenced_assemblies(string assembly, TestFramework expected)
    {
        Assert.Equal(expected, FrameworkDetector.DetectFramework([assembly, "System.Runtime"]));
    }

    [Fact]
    public void TUnit_is_always_on_the_testing_platform()
    {
        Assert.Equal(TestRunner.MicrosoftTestingPlatform,
            FrameworkDetector.DetectRunner(TestFramework.TUnit, ["TUnit.Core"], new Dictionary<string, string>()));
    }

    [Fact]
    public void Opting_into_the_testing_platform_switches_the_runner()
    {
        var properties = new Dictionary<string, string> { ["TestingPlatformDotnetTestSupport"] = "true" };

        Assert.Equal(TestRunner.MicrosoftTestingPlatform,
            FrameworkDetector.DetectRunner(TestFramework.XUnitV3, ["xunit.v3.core"], properties));
    }

    [Fact]
    public void The_vstest_bridge_is_the_default_for_xunit_v3()
    {
        Assert.Equal(TestRunner.VsTest, FrameworkDetector.DetectRunner(
            TestFramework.XUnitV3,
            ["xunit.v3.core", "Microsoft.VisualStudio.TestPlatform.ObjectModel"],
            new Dictionary<string, string>()));
    }
}
