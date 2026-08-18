using Microsoft.CodeAnalysis;
using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;

namespace Tia.Core.Tests;

/// <summary>
/// Reading a name back out of a graph key, which is how the base revision of a diff is bound
/// without a compilation.
/// </summary>
/// <remarks>
/// Written against real compilations rather than hand-written keys. The point of these is that the
/// answer agrees with Roslyn's documentation id, and a hand-written expectation only proves the
/// parser agrees with whoever wrote the test.
/// </remarks>
public class SymbolKeyReadingTests
{
    private const string Source = """
        namespace App.Deep
        {
            public class Widget
            {
                public const int Limit = 3;
                public int Count { get; set; }
                public int Value() => 1;
                public int Value(int scale) => scale;
                public T Convert<T>(T input) => input;
                public int this[int index] => index;
                public event System.Action? Changed;
                public static Widget operator +(Widget a, Widget b) => a;

                public class Nested { public void Inner() { } }
            }

            public class Widget<T> { public class Inner { public void Deep() { } } }

            public interface IThing { void Do(); }

            public class Thing : IThing { void IThing.Do() { } }
        }
        """;

    [Theory]
    [InlineData("App.Deep.Widget", "App.Deep.Widget")]
    [InlineData("App.Deep.Widget+Nested", "App.Deep.Widget.Nested")]
    // A type nested inside a generic one. Cutting arity once, rather than per segment, would
    // answer "App.Deep.Widget" here - a different type that exists.
    [InlineData("App.Deep.Widget`1+Inner", "App.Deep.Widget.Inner")]
    [InlineData("App.Deep.Widget`1", "App.Deep.Widget")]
    public void A_type_key_reads_back_as_its_name_path(string metadataName, string expected)
    {
        var compilation = CompilationHarness.CompileValid(Source, "Lib");

        Assert.Equal(expected, SymbolKeys.TypePathOf(CompilationHarness.KeyOf(compilation, metadataName)));
    }

    [Theory]
    [InlineData("Value", "Value")]
    [InlineData("Limit", "Limit")]
    [InlineData("Count", "Count")]
    [InlineData("Changed", "Changed")]
    // A generic method carries its arity in the id; the declaration in a base-revision tree does not.
    [InlineData("Convert", "Convert")]
    // An indexer is named Item whatever the source called it.
    [InlineData("this[]", "Item")]
    [InlineData("op_Addition", "op_Addition")]
    public void A_member_key_reads_back_as_its_declared_name(string memberName, string expected)
    {
        var compilation = CompilationHarness.CompileValid(Source, "Lib");

        Assert.Equal(expected, SymbolKeys.SimpleNameOf(CompilationHarness.KeyOf(compilation, "App.Deep.Widget", memberName)));
    }

    [Fact]
    public void An_explicit_implementation_reads_back_as_the_member_it_implements()
    {
        // The id spells it App#Deep#IThing#Do, because a documentation id cannot carry the dots.
        // The name a base-revision tree offers is just Do, so that is what has to come back.
        var compilation = CompilationHarness.CompileValid(Source, "Lib");
        var key = CompilationHarness.KeyOf(compilation, "App.Deep.Thing", "App.Deep.IThing.Do");

        Assert.Equal("Do", SymbolKeys.SimpleNameOf(key));
    }

    [Fact]
    public void A_constructor_keeps_its_metadata_name()
    {
        // And that is the point: the base-revision tree offers the type's own identifier for a
        // constructor, which matches neither "#ctor" nor "ctor". The lookup finds nothing and
        // falls through to the declaring type, which fans out to every member - the sound answer,
        // and the one this used to give when it had a compilation to ask.
        var compilation = CompilationHarness.CompileValid(Source, "Lib");
        var key = CompilationHarness.KeyOf(compilation, "App.Deep.Widget", ".ctor");

        Assert.Equal("#ctor", SymbolKeys.SimpleNameOf(key));
    }

    [Theory]
    // No separator: not a key at all.
    [InlineData("nonsense")]
    // The fallback form, which is a display string and not an id. Reading it as one would produce
    // a name that matches nothing, or worse, one that matches the wrong thing.
    [InlineData("Lib|Method:App.Widget.Value(int)")]
    [InlineData("Lib|!:App.Missing")]
    public void An_unreadable_key_answers_nothing_rather_than_guessing(string key)
    {
        Assert.Null(SymbolKeys.TypePathOf(key));
        Assert.Null(SymbolKeys.SimpleNameOf(key));
    }

    [Fact]
    public void A_member_key_is_not_a_type_path_and_a_type_key_has_no_member_name()
    {
        var compilation = CompilationHarness.CompileValid(Source, "Lib");

        Assert.Null(SymbolKeys.TypePathOf(CompilationHarness.KeyOf(compilation, "App.Deep.Widget", "Value")));
        Assert.Equal("Widget", SymbolKeys.SimpleNameOf(CompilationHarness.KeyOf(compilation, "App.Deep.Widget")));
    }

    [Fact]
    public void The_index_finds_a_type_and_its_overloads_by_name()
    {
        var compilation = CompilationHarness.CompileValid(Source, "Lib");
        var graph = CompilationHarness.BuildGraph(compilation, "Lib");

        var index = SourceTypeIndex.FromGraph(graph, "Lib");

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Deep.Widget"), index.FindTypes("App.Deep.Widget"));

        // Both overloads, because without binding they are indistinguishable - and marking one of
        // two is how a caller of the other is missed.
        var typeKey = CompilationHarness.KeyOf(compilation, "App.Deep.Widget");
        Assert.Equal(2, index.FindMembers(typeKey, "Value").Count);
        Assert.Empty(index.FindMembers(typeKey, "NoSuchMember"));
    }

    [Fact]
    public void Two_types_sharing_a_name_path_are_both_reported()
    {
        // Widget and Widget<T> differ only in arity, which a base-revision tree cannot establish.
        // Reporting one of them would decide by whichever the graph happened to enumerate first.
        var compilation = CompilationHarness.CompileValid(Source, "Lib");
        var index = SourceTypeIndex.FromGraph(CompilationHarness.BuildGraph(compilation, "Lib"), "Lib");

        Assert.Equal(
            [
                CompilationHarness.KeyOf(compilation, "App.Deep.Widget"),
                CompilationHarness.KeyOf(compilation, "App.Deep.Widget`1"),
            ],
            index.FindTypes("App.Deep.Widget").OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void The_old_side_still_finds_a_deleted_member_through_the_graph()
    {
        // End to end for the path these helpers exist to serve: a member that is gone at HEAD
        // widens to its declaring type, with no compilation involved in answering it.
        const string current = "namespace App { public class Widget { public int Kept() => 1; } }";
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

        var compilation = CompilationHarness.CompileValid(current, "Lib");
        var index = SourceTypeIndex.FromGraph(CompilationHarness.BuildGraph(compilation, "Lib"), "Lib");

        var changed = new OldSideResolver(index).Resolve(atBase, [new LineRange(6, 6)], "Lib", "Widget.cs");

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Widget"), changed.Keys);
    }
}
