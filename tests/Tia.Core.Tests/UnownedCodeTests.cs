using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Analysis;
using Tia.Core.Diff;

namespace Tia.Core.Tests;

/// <summary>
/// Changed lines that no declaration owns (#35). Top-level statements and assembly attributes used
/// to be recorded as unmapped and select nothing. They now widen their project. Everything else
/// outside a declaration keeps the rule it had, which the second half of this class pins: a using,
/// a comment or a namespace line beside the types of an ordinary file must not widen anything.
/// </summary>
public sealed class UnownedCodeTests
{
    private const string FilePath = "/repo/Program.cs";

    /// <summary>A minimal-API <c>Program.cs</c> with no <c>partial class Program</c>.</summary>
    private const string TopLevelProgram = """
        using System;

        var greeting = "Hello, " + args.Length;
        Console.WriteLine(greeting);

        // The last statement.
        return 0;
        """;

    /// <summary>The same with the declaration functional tests add so they can name the entry point.</summary>
    private const string TopLevelProgramWithPartial = """
        using System;

        var greeting = "Hello, " + args.Length;
        Console.WriteLine(greeting);
        return 0;

        public partial class Program;
        """;

    private const string AssemblyInfo = """
        using System.Reflection;

        // Read by the tests at run time.
        [assembly: AssemblyMetadata("issue", "35")]
        """;

    private const string TypedFile = """
        // Licensed under the MIT licence.
        using System;
        using System.Text;

        namespace App;

        public class Widget
        {
            public int Value() => 1;
        }

        public class Gadget
        {
            public int Value() => 2;
        }
        """;

    [Theory]
    // The first statement.
    [InlineData(3, 3)]
    // The last, which a hunk that only appends to a Program.cs touches.
    [InlineData(7, 7)]
    public void A_changed_top_level_statement_widens_its_project(int start, int end)
    {
        var changed = Resolve(TopLevelProgram, OutputKind.ConsoleApplication, new LineRange(start, end));

        var widening = Assert.Single(changed.ProjectWide);
        Assert.Equal(ProjectWideCause.Unmapped, widening.Cause);
        Assert.Equal("App", widening.ProjectName);
        Assert.True(widening.WidensProject);
        Assert.Contains("top-level statement", widening.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hunk_from_the_statements_into_a_partial_Program_still_widens()
    {
        // The hunk touches a declaration, which used to settle it. Marking Program is not enough:
        // WebApplicationFactory<T> accepts any type in the assembly, so a test that names another
        // one still runs these statements without ever referencing Program.
        var changed = Resolve(TopLevelProgramWithPartial, OutputKind.ConsoleApplication, new LineRange(5, 7));

        Assert.Contains(changed.ProjectWide, w => w.Cause == ProjectWideCause.Unmapped);
    }

    [Fact]
    public void A_statement_beside_a_partial_Program_widens_on_its_own()
    {
        var changed = Resolve(TopLevelProgramWithPartial, OutputKind.ConsoleApplication, new LineRange(3, 3));

        Assert.Contains(changed.ProjectWide, w => w.Cause == ProjectWideCause.Unmapped);
    }

    [Fact]
    public void A_using_change_in_a_file_of_top_level_statements_widens()
    {
        // A using rebinds every type in an ordinary file, and here what it rebinds is statements.
        var changed = Resolve(TopLevelProgram, OutputKind.ConsoleApplication, new LineRange(1, 1));

        Assert.Contains(changed.ProjectWide, w => w.Cause == ProjectWideCause.Unmapped);
    }

    [Theory]
    // The attribute itself.
    [InlineData(4, 4)]
    // Its using, which decides what the attribute's name binds to.
    [InlineData(1, 1)]
    // A new file, which the diff reads as changed end to end.
    [InlineData(1, int.MaxValue)]
    public void A_file_of_assembly_attributes_widens_its_project(int start, int end)
    {
        var changed = Resolve(AssemblyInfo, OutputKind.DynamicallyLinkedLibrary, new LineRange(start, end));

        var widening = Assert.Single(changed.ProjectWide);
        Assert.Equal(ProjectWideCause.Unmapped, widening.Cause);
        Assert.Empty(changed.Keys);
    }

    [Fact]
    public void An_assembly_attribute_above_a_type_widens_and_the_type_keeps_its_own_seed()
    {
        const string source = """
            [assembly: System.Reflection.AssemblyMetadata("issue", "35")]

            namespace App
            {
                public class Widget { public int Value() => 1; }
            }
            """;

        // A new file: one range over the attribute and the type both.
        var changed = Resolve(source, OutputKind.DynamicallyLinkedLibrary, new LineRange(1, int.MaxValue));

        Assert.Contains(changed.ProjectWide, w => w.Cause == ProjectWideCause.Unmapped);
        Assert.NotEmpty(changed.Keys);
    }

    [Theory]
    // A licence header.
    [InlineData(1, 1)]
    // A using directive.
    [InlineData(2, 2)]
    // The header and the usings together.
    [InlineData(1, 3)]
    // The namespace line.
    [InlineData(5, 5)]
    // The blank line between two types, which is what deleting a third one there leaves.
    [InlineData(11, 11)]
    public void A_change_beside_the_types_of_an_ordinary_file_marks_them_and_widens_nothing(int start, int end)
    {
        var changed = Resolve(TypedFile, OutputKind.DynamicallyLinkedLibrary, new LineRange(start, end));

        Assert.Empty(changed.ProjectWide);
        Assert.Equal(2, changed.Keys.Count);
    }

    [Fact]
    public void A_global_using_still_widens_as_a_global_using()
    {
        const string source = """
            global using System.Text;

            namespace App
            {
                public class Widget { public int Value() => 1; }
            }
            """;

        var changed = Resolve(source, OutputKind.DynamicallyLinkedLibrary, new LineRange(1, 1));

        var widening = Assert.Single(changed.ProjectWide);
        Assert.Equal(ProjectWideCause.GlobalUsing, widening.Cause);
    }

    [Fact]
    public void A_global_using_in_a_hunk_that_reaches_a_type_still_widens()
    {
        // The type in the same hunk used to settle it, and the global using went unread.
        const string source = """
            global using System.Text;

            namespace App
            {
                public class Widget { public int Value() => 1; }
            }
            """;

        var changed = Resolve(source, OutputKind.DynamicallyLinkedLibrary, new LineRange(1, 5));

        Assert.Contains(changed.ProjectWide, w => w.Cause == ProjectWideCause.GlobalUsing);
        Assert.NotEmpty(changed.Keys);
    }

    [Fact]
    public void A_file_with_nothing_to_rebind_is_a_note_not_a_widening()
    {
        // No type, no statement, no attribute: nothing here binds, so nothing can have changed.
        // It used to be reported as an Unmapped widening that widened nothing.
        const string source = """
            // Nothing but directives.
            using System;

            namespace App;
            """;

        var changed = Resolve(source, OutputKind.DynamicallyLinkedLibrary, new LineRange(2, 2));

        Assert.Empty(changed.ProjectWide);
        Assert.Empty(changed.Keys);
        Assert.Contains(changed.Notes, n => n.Contains("seeds nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_deleted_file_of_assembly_attributes_widens_from_the_old_side()
    {
        // The file is gone, so there is no new tree to look at. Only the base revision says what
        // it held - an InternalsVisibleTo, say, that the tests needed.
        var index = SourceTypeIndex.FromGraph(
            CompilationHarness.BuildGraph(CompilationHarness.Compile("namespace App { public class Kept { } }"), "App"), "App");

        var changed = new OldSideResolver(index).Resolve(AssemblyInfo, [new LineRange(1, 4)], "App", "src/App/AssemblyInfo.cs");

        Assert.Contains(changed.ProjectWide, w => w.Cause == ProjectWideCause.Unmapped);
    }

    [Fact]
    public void A_removed_top_level_statement_widens_from_the_old_side()
    {
        const string current = """
            using System;

            Console.WriteLine("kept");
            """;

        const string atBase = """
            using System;

            Console.WriteLine("kept");
            Console.WriteLine("removed");
            """;

        var index = SourceTypeIndex.FromGraph(
            CompilationHarness.BuildGraph(Compile(current, OutputKind.ConsoleApplication), "App"), "App");

        var changed = new OldSideResolver(index).Resolve(atBase, [new LineRange(4, 4)], "App", "src/App/Program.cs");

        Assert.Contains(changed.ProjectWide, w => w.Cause == ProjectWideCause.Unmapped);
    }

    [Fact]
    public void Both_sides_of_a_modified_statement_report_one_widening()
    {
        // Each side sees the change, from its own revision, and on its own lines. Reporting both
        // would read as two changes.
        var newSide = Resolve(TopLevelProgram, OutputKind.ConsoleApplication, new LineRange(3, 3));
        var index = SourceTypeIndex.FromGraph(
            CompilationHarness.BuildGraph(Compile(TopLevelProgram, OutputKind.ConsoleApplication), "App"), "App");
        var oldSide = new OldSideResolver(index).Resolve(
            TopLevelProgram.Replace("\"Hello, \"", "\"Hi, \"", StringComparison.Ordinal), [new LineRange(3, 3)], "App", "Program.cs");

        var merged = new SymbolChangeSet();
        merged.Merge(newSide);
        merged.Merge(oldSide);

        Assert.Single(merged.ProjectWide);
    }

    [Fact]
    public void Generated_documents_keep_the_rules_they_had()
    {
        // Seeding generated code treats a whole document as changed. The Razor generator puts an
        // assembly attribute into every view, so reading those as changed would widen every such
        // project on every change - the blunt fallback that seeding generated code replaced.
        const string source = """
            [assembly: System.Reflection.AssemblyMetadata("view", "Index")]

            namespace App
            {
                public class IndexView { public int Render() => 1; }
            }
            """;

        var compilation = Compile(source, OutputKind.DynamicallyLinkedLibrary);
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());

        var changed = new ChangedSymbolResolver { GeneratedDocuments = true }
            .Resolve(model, [new LineRange(1, int.MaxValue)], "App");

        Assert.Empty(changed.ProjectWide);
        Assert.NotEmpty(changed.Keys);
    }

    /// <summary>
    /// Resolves from stored positions, which is the path every file on disk takes, and asserts the
    /// semantic-model resolver gives the same answer: the two have to agree line for line.
    /// </summary>
    private static SymbolChangeSet Resolve(string source, OutputKind kind, LineRange range)
    {
        var compilation = Compile(source, kind);
        var tree = compilation.SyntaxTrees.Single();

        var expected = new ChangedSymbolResolver().Resolve(compilation.GetSemanticModel(tree), [range], "App");

        var symbols = new ReferenceGraphBuilder(new HashSet<string>(StringComparer.Ordinal) { compilation.AssemblyName! })
            .BuildSymbols(compilation, "App");

        var actual = DeclarationSiteResolver.Resolve(
            [.. symbols.Declarations.Where(d => d.FilePath == FilePath)],
            symbols.Graph,
            [range],
            "App",
            FilePath,
            tree.GetText().Lines.Count,
            isNewFile: false,
            () => tree);

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            expected.ProjectWide.Select(w => (w.Cause, w.Detail)).OrderBy(w => w.Detail, StringComparer.Ordinal),
            actual.ProjectWide.Select(w => (w.Cause, w.Detail)).OrderBy(w => w.Detail, StringComparer.Ordinal));
        Assert.Equal(expected.Notes.Order(StringComparer.Ordinal), actual.Notes.Order(StringComparer.Ordinal));

        return actual;
    }

    /// <summary>Compiled clean, as an executable when it has top-level statements to hold.</summary>
    private static CSharpCompilation Compile(string source, OutputKind kind)
    {
        var compilation = CompilationHarness.Compile(source, "App", FilePath);
        compilation = compilation.WithOptions(compilation.Options.WithOutputKind(kind));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }
}
