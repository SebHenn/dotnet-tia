using Microsoft.CodeAnalysis;
using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class DeletionOracleTests
{
    private const string Root = "/repo";

    private static Finding At(int line, string symbol, string kind = "method", string file = "Test.cs") =>
        new(file, line, kind, symbol, "P", symbol);

    private static DeletionVerdict Single(string source, Finding finding)
    {
        var compilation = CompilationHarness.CompileValid(source);
        return DeletionOracle.Check(Root, [new OracleProject("P", compilation, [])], [finding]).Single();
    }

    [Fact]
    public void A_declaration_nothing_uses_can_be_deleted_without_a_new_error()
    {
        var verdict = Single("""
            public static class Program
            {
                public static void Main() { }
                private static void Forgotten() { }
            }
            """, At(4, "Program.Forgotten()"));

        Assert.Equal(DeletionOutcome.Safe, verdict.Outcome);
    }

    [Fact]
    public void A_declaration_something_uses_breaks_the_build_and_the_verdict_names_the_error()
    {
        var verdict = Single("""
            public static class Program
            {
                public static void Main() => Used();
                private static void Used() { }
            }
            """, At(4, "Program.Used()"));

        Assert.Equal(DeletionOutcome.Broken, verdict.Outcome);
        Assert.Contains(verdict.Errors, error => error.Contains("CS0103", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_named_field_goes_when_several_share_a_declaration()
    {
        var verdict = Single("""
            public static class Program
            {
                private static int unused, used;
                public static int Main() => used;
            }
            """, At(3, "Program.unused", "field"));

        Assert.Equal(DeletionOutcome.Safe, verdict.Outcome);
    }

    [Fact]
    public void A_use_in_a_project_that_references_the_declaring_one_counts()
    {
        var library = CompilationHarness.CompileValid("""
            public static class Lib
            {
                public static int Value() => 1;
            }
            """, "Lib", "/repo/Lib.cs");

        var app = CompilationHarness.Compile(
            "App",
            ("/repo/App.cs", "public static class App { public static int Run() => Lib.Value(); }"))
            .AddReferences(library.ToMetadataReference());
        Assert.Empty(app.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var verdict = DeletionOracle.Check(
            Root,
            [new OracleProject("Lib", library, []), new OracleProject("App", app, ["Lib"])],
            [At(3, "Lib.Value()", file: "Lib.cs")]).Single();

        Assert.Equal(DeletionOutcome.Broken, verdict.Outcome);
        Assert.Contains(verdict.Errors, error => error.StartsWith("App:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_error_the_project_already_had_is_not_blamed_on_the_deletion()
    {
        // Positions shift when a declaration goes, so the same error is matched by what it says.
        var compilation = CompilationHarness.Compile("""
            public static class Program
            {
                private static void Forgotten() { }
                public static void Main() => Missing();
            }
            """);

        var verdict = DeletionOracle.Check(Root, [new OracleProject("P", compilation, [])], [At(3, "Program.Forgotten()")]).Single();

        Assert.Equal(DeletionOutcome.Safe, verdict.Outcome);
    }

    [Fact]
    public void A_finding_that_matches_no_declaration_is_not_called_safe()
    {
        var verdict = Single("""
            public static class Program
            {
                public static void Main() { }
            }
            """, At(40, "Program.Nowhere()"));

        Assert.Equal(DeletionOutcome.Unlocated, verdict.Outcome);
    }

    [Fact]
    public void An_interface_member_and_its_only_implementation_are_dead_together()
    {
        var source = """
            internal interface IThing { int Value { get; } }
            internal sealed class Thing : IThing { int IThing.Value => 1; }
            internal static class Program { public static void Main() { _ = new Thing(); } }
            """;
        var compilation = CompilationHarness.CompileValid(source);
        var project = new OracleProject("P", compilation, []);

        var verdicts = DeletionOracle.Check(Root, [project], [At(1, "IThing.Value", "property"), At(2, "Thing.Value", "property")]);

        Assert.All(verdicts, verdict => Assert.Equal(DeletionOutcome.Safe, verdict.Outcome));
    }

    [Fact]
    public void A_used_member_is_still_broken_when_it_is_listed_beside_unrelated_findings()
    {
        var compilation = CompilationHarness.CompileValid("""
            public static class Program
            {
                public static void Main() => Used();
                private static void Used() { }
                private static void Forgotten() { }
            }
            """);

        var verdicts = DeletionOracle.Check(Root, [new OracleProject("P", compilation, [])], [At(4, "Program.Used()"), At(5, "Program.Forgotten()")]);

        Assert.Equal(DeletionOutcome.Broken, verdicts[0].Outcome);
        Assert.Equal(DeletionOutcome.Safe, verdicts[1].Outcome);
    }

    [Fact]
    public void A_cluster_spread_over_two_files_is_dead_together()
    {
        // The attribute class is only applied by Tool, and Tool is only a finding of its own:
        // deleting the attribute alone breaks Tool, deleting both breaks nothing.
        var compilation = CompilationHarness.Compile(
            "P",
            ("/repo/Mark.cs", "internal sealed class MarkAttribute : System.Attribute { }"),
            ("/repo/Tool.cs", "[Mark] internal sealed class Tool { }"),
            ("/repo/Program.cs", "public static class Program { public static void Main() { } }"));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var mark = At(1, "MarkAttribute", "type", "Mark.cs");
        var tool = At(1, "Tool", "type", "Tool.cs");
        var verdicts = DeletionOracle.Check(Root, [new OracleProject("P", compilation, [])], [mark], [mark, tool]);

        Assert.Equal(DeletionOutcome.Safe, verdicts.Single().Outcome);
    }

    [Fact]
    public void A_declaration_used_from_another_file_is_still_broken_when_the_user_is_not_a_finding()
    {
        var compilation = CompilationHarness.Compile(
            "P",
            ("/repo/Mark.cs", "internal sealed class MarkAttribute : System.Attribute { }"),
            ("/repo/Program.cs", "[Mark] public static class Program { public static void Main() { } }"));

        var verdict = DeletionOracle.Check(
            Root, [new OracleProject("P", compilation, [])], [At(1, "MarkAttribute", "type", "Mark.cs")]).Single();

        Assert.Equal(DeletionOutcome.Broken, verdict.Outcome);
    }

    [Fact]
    public void A_member_of_a_generic_nested_type_is_found()
    {
        var verdict = Single("""
            internal static class Outer
            {
                internal sealed class Box<T>
                {
                    public bool Forgotten() => true;
                }
            }
            public static class Program { public static void Main() { _ = new Outer.Box<int>(); } }
            """, At(5, "Outer.Box<T>.Forgotten()"));

        Assert.Equal(DeletionOutcome.Safe, verdict.Outcome);
    }

    [Fact]
    public void An_operator_is_found_by_its_line()
    {
        var verdict = Single("""
            internal readonly struct Money
            {
                public static Money operator +(Money a, Money b) => a;
            }
            internal static class Program { public static void Main() { _ = default(Money); } }
            """, At(3, "Money.operator +(Money, Money)"));

        Assert.Equal(DeletionOutcome.Safe, verdict.Outcome);
    }

    [Fact]
    public void A_doc_comment_that_names_the_declaration_is_a_warning_not_a_use()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            """
            internal static class Program
            {
                /// <summary>See <see cref="Forgotten"/>.</summary>
                public static void Main() { }
                private static void Forgotten() { }
            }
            """,
            new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
            "/repo/Test.cs");
        var plain = CompilationHarness.CompileValid("public static class Placeholder { }");
        var strict = plain.RemoveAllSyntaxTrees().AddSyntaxTrees(tree)
            .WithOptions(plain.Options.WithGeneralDiagnosticOption(ReportDiagnostic.Error));
        Assert.Empty(strict.GetDiagnostics());

        var verdict = DeletionOracle.Check(Root, [new OracleProject("P", strict, [])], [At(5, "Program.Forgotten()")]).Single();

        Assert.Equal(DeletionOutcome.Warned, verdict.Outcome);
    }

    [Fact]
    public void Every_part_of_a_partial_type_goes_with_the_finding()
    {
        // `Docs` is one finding in the scan. Its second part still names Helper, so deleting only
        // the first part would make Helper look used.
        var compilation = CompilationHarness.Compile(
            "P",
            ("/repo/Helper.cs", "internal sealed class Helper { }"),
            ("/repo/Docs.A.cs", "internal static partial class Docs { }"),
            ("/repo/Docs.B.cs", "internal static partial class Docs { public static Helper Make() => new Helper(); }"),
            ("/repo/Program.cs", "public static class Program { public static void Main() { } }"));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var helper = At(1, "Helper", "type", "Helper.cs");
        var docs = At(1, "Docs", "type", "Docs.A.cs");
        var verdicts = DeletionOracle.Check(Root, [new OracleProject("P", compilation, [])], [helper], [helper, docs]);

        Assert.Equal(DeletionOutcome.Safe, verdicts.Single().Outcome);
    }

    [Fact]
    public void A_companion_in_a_dependent_project_goes_with_the_finding()
    {
        // Nothing runs Impl, so the interface and its implementation are dead together - across a
        // project boundary. Deleting the interface alone breaks the dependent; both together do not.
        var library = CompilationHarness.CompileValid("public interface IPlug { }", "Lib", "/repo/Lib.cs");
        var app = CompilationHarness.Compile(
            "App",
            ("/repo/App.cs", "internal sealed class Impl : IPlug { } public static class App { public static void Main() { } }"))
            .AddReferences(library.ToMetadataReference());
        Assert.Empty(app.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var plug = At(1, "IPlug", "type", "Lib.cs");
        var impl = At(1, "Impl", "type", "App.cs");
        var verdicts = DeletionOracle.Check(
            Root,
            [new OracleProject("Lib", library, []), new OracleProject("App", app, ["Lib"])],
            [plug],
            [plug, impl]);

        Assert.Equal(DeletionOutcome.Safe, verdicts.Single().Outcome);
    }

    [Fact]
    public void A_use_two_projects_away_counts()
    {
        // App uses Lib only through Mid's public type, which exposes Lib.Value's result.
        var library = CompilationHarness.CompileValid("public static class Lib { public static int Value() => 1; }", "Lib", "/repo/Lib.cs");
        var mid = CompilationHarness.Compile("Mid", ("/repo/Mid.cs", "public static class Mid { public static int Get() => Lib.Value(); }"))
            .AddReferences(library.ToMetadataReference());
        var app = CompilationHarness.Compile("App", ("/repo/App.cs", "public static class App { public static int Run() => Mid.Get(); }"))
            .AddReferences(mid.ToMetadataReference());
        Assert.Empty(app.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        // Mid's source is what breaks when Lib.Value goes, so the error is in Mid and found
        // through the direct dependent; App is then compiled against the edited Mid as well.
        var verdict = DeletionOracle.Check(
            Root,
            [new OracleProject("Lib", library, []), new OracleProject("Mid", mid, ["Lib"]), new OracleProject("App", app, ["Mid"])],
            [At(1, "Lib.Value()", file: "Lib.cs")]).Single();

        Assert.Equal(DeletionOutcome.Broken, verdict.Outcome);
        Assert.Contains(verdict.Errors, error => error.StartsWith("Mid:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_use_in_a_project_that_reaches_the_declaring_one_only_through_another_counts()
    {
        // App lists only Mid, but its compilation holds Lib too: Derived's base class lives there.
        var library = CompilationHarness.CompileValid("public class Base { public void Hello() { } }", "Lib", "/repo/Lib.cs");
        var mid = CompilationHarness.Compile("Mid", ("/repo/Mid.cs", "public class Derived : Base { }"))
            .AddReferences(library.ToMetadataReference());
        var app = CompilationHarness.Compile("App", ("/repo/App.cs", "public static class App { public static void Run() => new Derived().Hello(); }"))
            .AddReferences(mid.ToMetadataReference(), library.ToMetadataReference());
        Assert.Empty(app.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var verdict = DeletionOracle.Check(
            Root,
            [new OracleProject("Lib", library, []), new OracleProject("Mid", mid, ["Lib"]), new OracleProject("App", app, ["Mid"])],
            [At(1, "Base.Hello()", file: "Lib.cs")]).Single();

        Assert.Equal(DeletionOutcome.Broken, verdict.Outcome);
        Assert.Contains(verdict.Errors, error => error.StartsWith("App:", StringComparison.Ordinal));
    }
}
