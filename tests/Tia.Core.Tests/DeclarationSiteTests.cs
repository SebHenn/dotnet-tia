using Microsoft.CodeAnalysis;
using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;

namespace Tia.Core.Tests;

/// <summary>
/// Seeds resolved from stored declaration positions have to be the seeds a semantic model would
/// have produced. Everything downstream takes the change set on trust, so a divergence here is a
/// missed test with nothing to show for it.
/// </summary>
public class DeclarationSiteTests
{
    private const string Path = "/repo/Widget.cs";

    /// <summary>
    /// The property that matters: same file, same lines, same answer - whichever resolver produced
    /// it. Written as one theory over the shapes that have their own rule, because each of those
    /// rules is a correctness rule and none of them is a refinement.
    /// </summary>
    [Theory]
    // A method body: the member alone.
    [InlineData(9, 9)]
    // The type header: type-wide, and every member with it.
    [InlineData(3, 3)]
    // A constant: the declaring type, plus a project-wide widening for inlining.
    [InlineData(5, 5)]
    // A field that is not a constant.
    [InlineData(6, 6)]
    // A property.
    [InlineData(7, 7)]
    // A using directive, outside every declaration.
    [InlineData(1, 1)]
    // A range that spans the whole file.
    [InlineData(1, 40)]
    // Past the end of the file, which a trailing deletion produces.
    [InlineData(400, 420)]
    public void The_two_resolvers_agree(int start, int end)
    {
        const string source = """
            using System;

            public class Widget
            {
                public const int Limit = 3;
                private int _count;
                public int Count => _count;

                public int Value() => 1;
                public int Value(int scale) => scale;

                public class Nested
                {
                    public void Inner() { }
                }
            }
            """;

        var compilation = CompilationHarness.CompileValid(source, "Lib", Path);
        var ranges = new[] { new LineRange(start, end) };

        var expected = new ChangedSymbolResolver().Resolve(
            compilation.GetSemanticModel(compilation.SyntaxTrees.Single()), ranges, "Lib");

        var actual = ResolveFromSites(compilation, ranges, isNewFile: false);

        Assert.Equal(Sorted(expected.Keys), Sorted(actual.Keys));
        Assert.Equal(
            expected.ProjectWide.Select(w => (w.Cause, w.Detail)).OrderBy(w => w.Detail, StringComparer.Ordinal),
            actual.ProjectWide.Select(w => (w.Cause, w.Detail)).OrderBy(w => w.Detail, StringComparer.Ordinal));
        Assert.Equal(Sorted(expected.UnmappedChanges), Sorted(actual.UnmappedChanges));
    }

    [Fact]
    public void A_global_using_widens_the_project_from_stored_positions()
    {
        // Not a declaration, so no site can carry it, and the branch that decides it is the one
        // place this resolver still has to look at syntax. Without it a changed global using would
        // fall through to "mark every type in the file" and miss every other file in the project.
        const string source = """
            global using System;

            public class Widget
            {
                public int Value() => 1;
            }
            """;

        var compilation = CompilationHarness.CompileValid(source, "Lib", Path);

        var changed = ResolveFromSites(compilation, [new LineRange(1, 1)], isNewFile: false);

        Assert.Contains(changed.ProjectWide, w => w.Cause == ProjectWideCause.GlobalUsing);
    }

    [Fact]
    public void A_constant_in_a_new_file_does_not_widen()
    {
        // Nothing could have inlined it, because nothing could reference it before the file
        // existed. The declaring type is still marked; only the widening is dropped.
        const string source = """
            public class Widget
            {
                public const int Limit = 3;
            }
            """;

        var compilation = CompilationHarness.CompileValid(source, "Lib", Path);

        var changed = ResolveFromSites(compilation, [new LineRange(3, 3)], isNewFile: true);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "Widget"), changed.Keys);
        Assert.DoesNotContain(changed.ProjectWide, w => w.Cause == ProjectWideCause.ConstantInlining);
    }

    [Fact]
    public void A_partial_type_records_a_site_in_every_file_that_declares_it()
    {
        // One node, several declarations. SymbolNode keeps a single FilePath - one is enough for
        // what reads it - so a resolver keyed on the node's file would find nothing for a change
        // in the other half. Sites are per declaration, which is what makes that work.
        var compilation = CompilationHarness.Compile(
            "Lib",
            ("/repo/Widget.Part1.cs", "public partial class Widget { public int A() => 1; }"),
            ("/repo/Widget.Part2.cs", "public partial class Widget { public int B() => 2; }"));

        var declarations = new ReferenceGraphBuilder(new HashSet<string>(StringComparer.Ordinal) { "Lib" })
            .BuildSymbols(compilation, "Lib").Declarations;

        var typeKey = CompilationHarness.KeyOf(compilation, "Widget");
        var files = declarations.Where(d => d.Key == typeKey).Select(d => d.FilePath).OrderBy(f => f, StringComparer.Ordinal);

        Assert.Equal(["/repo/Widget.Part1.cs", "/repo/Widget.Part2.cs"], files);
    }

    [Fact]
    public void A_generated_tree_records_no_site()
    {
        // A tree with no path on disk can never be named by a diff, so a site for it could never
        // be looked up. Its declarations are still nodes; only the position is dropped.
        var compilation = CompilationHarness.Compile("Lib", (string.Empty, "public class Generated { }"));

        var declarations = new ReferenceGraphBuilder(new HashSet<string>(StringComparer.Ordinal) { "Lib" })
            .BuildSymbols(compilation, "Lib").Declarations;

        Assert.Empty(declarations);
    }

    private static SymbolChangeSet ResolveFromSites(Compilation compilation, IReadOnlyList<LineRange> ranges, bool isNewFile)
    {
        var symbols = new ReferenceGraphBuilder(new HashSet<string>(StringComparer.Ordinal) { compilation.AssemblyName! })
            .BuildSymbols(compilation, "Lib");

        var tree = compilation.SyntaxTrees.Single();

        return DeclarationSiteResolver.Resolve(
            [.. symbols.Declarations.Where(d => d.FilePath == Path)],
            symbols.Graph,
            ranges,
            "Lib",
            Path,
            tree.GetText().Lines.Count,
            isNewFile,
            () => tree);
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> values) =>
        values.OrderBy(v => v, StringComparer.Ordinal);
}
