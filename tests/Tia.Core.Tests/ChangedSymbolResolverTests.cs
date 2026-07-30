using Microsoft.CodeAnalysis;
using Tia.Core.Analysis;
using Tia.Core.Diff;

namespace Tia.Core.Tests;

public sealed class ChangedSymbolResolverTests
{
    [Fact]
    public void A_changed_line_maps_to_the_member_that_declares_it()
    {
        const string source = """
            namespace App
            {
                public class Calculator
                {
                    public int Add(int a, int b) => a + b;

                    public int Subtract(int a, int b) => a - b;
                }
            }
            """;

        var changed = Resolve(source, new LineRange(5, 5));

        Assert.Contains(KeyOf(source, "App.Calculator", "Add"), changed.Keys);
        Assert.DoesNotContain(KeyOf(source, "App.Calculator", "Subtract"), changed.Keys);
    }

    [Fact]
    public void Changing_a_const_widens_to_the_declaring_type_and_the_project()
    {
        // Callers inline the value at compile time and carry no reference to the field, so a
        // call-graph walk finds nothing. This is the case a naive implementation gets wrong.
        const string source = """
            namespace App
            {
                public class Limits
                {
                    public const int MaxRetries = 3;

                    public int Unrelated() => 1;
                }
            }
            """;

        var changed = Resolve(source, new LineRange(5, 5));

        Assert.Contains(KeyOf(source, "App.Limits"), changed.Keys);
        Assert.Contains(changed.ProjectWide, c => c.Cause == ProjectWideCause.ConstantInlining);
    }

    [Fact]
    public void Changing_an_enum_member_widens_the_same_way()
    {
        const string source = """
            namespace App
            {
                public enum Mode
                {
                    Fast,
                    Slow,
                }
            }
            """;

        var changed = Resolve(source, new LineRange(5, 5));

        Assert.Contains(KeyOf(source, "App.Mode"), changed.Keys);
        Assert.Contains(changed.ProjectWide, c => c.Cause == ProjectWideCause.ConstantInlining);
    }

    [Fact]
    public void Changing_a_base_type_list_marks_the_whole_type()
    {
        const string source = """
            namespace App
            {
                public interface IMarker { }

                public class Widget
                    : IMarker
                {
                    public int Value() => 1;
                }
            }
            """;

        var changed = Resolve(source, new LineRange(6, 6));

        Assert.Contains(KeyOf(source, "App.Widget"), changed.Keys);
    }

    [Fact]
    public void Changing_an_attribute_marks_the_annotated_symbol_not_the_attribute_class()
    {
        const string source = """
            namespace App
            {
                public class MarkAttribute : System.Attribute { }

                public class Widget
                {
                    [Mark]
                    public int Value() => 1;
                }
            }
            """;

        var changed = Resolve(source, new LineRange(7, 7));

        Assert.Contains(KeyOf(source, "App.Widget", "Value"), changed.Keys);
        Assert.DoesNotContain(KeyOf(source, "App.MarkAttribute"), changed.Keys);
    }

    [Fact]
    public void Partial_classes_contribute_from_every_part()
    {
        const string partOne = """
            namespace App { public partial class Widget { public int First() => 1; } }
            """;
        const string partTwo = """
            namespace App { public partial class Widget { public int Second() => 2; } }
            """;

        var compilation = CompilationHarness.Compile("TestAsm", ("/repo/A.cs", partOne), ("/repo/B.cs", partTwo));
        var resolver = new ChangedSymbolResolver();

        var fromA = resolver.Resolve(ModelFor(compilation, "/repo/A.cs"), [new LineRange(1, 1)], "P");
        var fromB = resolver.Resolve(ModelFor(compilation, "/repo/B.cs"), [new LineRange(1, 1)], "P");

        var type = compilation.GetTypeByMetadataName("App.Widget")!;
        Assert.Contains(SymbolKeyOf(type.GetMembers("First")[0]), fromA.Keys);
        Assert.Contains(SymbolKeyOf(type.GetMembers("Second")[0]), fromB.Keys);
    }

    [Fact]
    public void Changing_a_global_using_widens_to_the_project()
    {
        const string source = """
            global using System.Text;

            namespace App
            {
                public class Widget { public int Value() => 1; }
            }
            """;

        var changed = Resolve(source, new LineRange(1, 1));

        Assert.Contains(changed.ProjectWide, c => c.Cause == ProjectWideCause.GlobalUsing);
    }

    [Fact]
    public void Changing_a_plain_using_marks_every_type_in_the_file()
    {
        const string source = """
            using System.Text;

            namespace App
            {
                public class Widget { public int Value() => 1; }
                public class Gadget { public int Value() => 2; }
            }
            """;

        var changed = Resolve(source, new LineRange(1, 1));

        Assert.Contains(KeyOf(source, "App.Widget"), changed.Keys);
        Assert.Contains(KeyOf(source, "App.Gadget"), changed.Keys);
    }

    [Fact]
    public void A_change_between_members_falls_back_to_the_type()
    {
        // This is what a deleted member looks like on the new side: a changed line that belongs to
        // no declaration. Marking the type is the only sound answer.
        const string source = """
            namespace App
            {
                public class Widget
                {
                    public int First() => 1;

                    public int Second() => 2;
                }
            }
            """;

        var changed = Resolve(source, new LineRange(6, 6));

        Assert.Contains(KeyOf(source, "App.Widget"), changed.Keys);
    }

    private static SymbolChangeSet Resolve(string source, params LineRange[] ranges)
    {
        var compilation = CompilationHarness.Compile(source);
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        return new ChangedSymbolResolver().Resolve(model, ranges, "TestProject");
    }

    private static SemanticModel ModelFor(Compilation compilation, string path) =>
        compilation.GetSemanticModel(compilation.SyntaxTrees.First(t => t.FilePath == path));

    private static string KeyOf(string source, string type, string? member = null) =>
        CompilationHarness.KeyOf(CompilationHarness.Compile(source), type, member);

    private static string SymbolKeyOf(ISymbol symbol) => Core.Model.SymbolKeys.For(symbol)!;
}
