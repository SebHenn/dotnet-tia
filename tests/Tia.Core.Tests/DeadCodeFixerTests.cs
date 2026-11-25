using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class DeadCodeFixerTests
{
    private const string Root = "/repo";

    private static Finding At(int line, string symbol, string kind = "method") =>
        new("Test.cs", line, kind, symbol, "P", symbol);

    private static FixPlan Plan(string source, params Finding[] findings) =>
        DeadCodeFixer.Plan(Root, [new OracleProject("P", CompilationHarness.CompileValid(source), [])], findings);

    [Fact]
    public void A_deleted_method_takes_its_doc_comment_and_leaves_the_rest_as_it_was()
    {
        var plan = Plan("""
            public static class Program
            {
                public static void Main() { }

                /// <summary>Nobody calls this.</summary>
                private static void Forgotten() { }

                private static void Kept() { }
            }
            """, At(6, "Program.Forgotten()"));

        Assert.Equal(["Program.Forgotten()"], plan.Deleted.Select(f => f.Symbol));
        Assert.Equal("""
            public static class Program
            {
                public static void Main() { }

                private static void Kept() { }
            }
            """, plan.Files["Test.cs"]);
    }

    [Fact]
    public void Two_findings_in_one_file_are_both_deleted_and_the_file_still_parses()
    {
        var plan = Plan("""
            public static class Program
            {
                public static void Main() { }
                private static void A() { }
                private static void B() { }
            }
            """, At(4, "Program.A()"), At(5, "Program.B()"));

        Assert.Equal(2, plan.Deleted.Count);
        Assert.DoesNotContain("void A", plan.Files["Test.cs"], StringComparison.Ordinal);
        Assert.DoesNotContain("void B", plan.Files["Test.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_and_a_member_inside_it_that_both_go_are_removed_once()
    {
        var plan = Plan("""
            public static class Program
            {
                public static void Main() { }
            }
            internal sealed class Gone
            {
                public void Inner() { }
            }
            """, At(5, "Gone", "type"), At(7, "Gone.Inner()"));

        Assert.Equal(2, plan.Deleted.Count);
        Assert.DoesNotContain("Gone", plan.Files["Test.cs"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("internal partial class Part { }", "Part", "type", "partial")]
    [InlineData("internal enum E { A = 1, B = 2 }", "E.A", "field", "enum member")]
    public void A_declaration_that_is_not_safe_to_edit_by_text_stays_and_says_why(string declaration, string symbol, string kind, string reason)
    {
        var plan = Plan($$"""
            public static class Program
            {
                public static void Main() { }
            }
            {{declaration}}
            """, At(5, symbol, kind));

        Assert.Empty(plan.Files);
        Assert.Contains(reason, plan.Skipped.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void One_field_of_several_in_a_declaration_stays()
    {
        var plan = Plan("""
            public static class Program
            {
                public static void Main() { }
                private static int a, b;
            }
            """, At(4, "Program.b", "field"));

        Assert.Empty(plan.Files);
        Assert.Contains("several fields", plan.Skipped.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declaration_beside_a_preprocessor_directive_stays()
    {
        var plan = Plan("""
            public static class Program
            {
                public static void Main() { }
            #if DEBUG
                private static void OnlyInDebug() { }
            #endif
            }
            """, At(5, "Program.OnlyInDebug()"));

        Assert.Empty(plan.Files);
    }
}
