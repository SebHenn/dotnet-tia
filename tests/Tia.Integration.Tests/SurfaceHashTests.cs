using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// The declaration surface decides whether a dependent project's cached graph fragment is reused.
/// A surface that moves too eagerly costs time; a surface that fails to move when it should is a
/// silent miss - a stale fragment merged into the graph as if it were current - so both directions
/// are pinned here.
/// </summary>
public sealed class SurfaceHashTests
{
    [Fact]
    public void A_method_body_change_leaves_the_surface_alone()
    {
        Assert.Equal(
            Surface("public class C { public int Value() { return 1; } }"),
            Surface("public class C { public int Value() { return 2; } }"));
    }

    [Fact]
    public void A_private_member_is_not_part_of_the_surface()
    {
        Assert.Equal(
            Surface("public class C { public int Value() => 1; }"),
            Surface("public class C { public int Value() => Helper(); private int Helper() => 1; }"));
    }

    [Fact]
    public void A_member_of_a_private_nested_type_is_not_part_of_the_surface()
    {
        Assert.Equal(
            Surface("public class C { private class Inner { } }"),
            Surface("public class C { private class Inner { public int Value() => 1; } }"));
    }

    [Fact]
    public void An_internal_member_is_part_of_the_surface()
    {
        // InternalsVisibleTo makes internals bindable from another assembly, and a test project
        // reaching into the library it tests is the normal reason a repository uses it.
        Assert.NotEqual(
            Surface("public class C { }"),
            Surface("public class C { internal int Value() => 1; }"));
    }

    [Fact]
    public void An_explicit_interface_implementation_is_part_of_the_surface()
    {
        // Roslyn reports these as private. They are reachable through the interface, so a surface
        // that skipped them would let a dependent keep an edge that no longer exists.
        const string withoutImplementation = """
            public interface IThing { int Value(); }
            public class C : IThing { public int Value() => 1; }
            """;

        const string withImplementation = """
            public interface IThing { int Value(); }
            public class C : IThing { int IThing.Value() => 1; }
            """;

        Assert.NotEqual(Surface(withoutImplementation), Surface(withImplementation));
    }

    [Fact]
    public void A_changed_signature_moves_the_surface()
    {
        Assert.NotEqual(
            Surface("public class C { public int Value(int x) => x; }"),
            Surface("public class C { public int Value(long x) => (int)x; }"));
    }

    [Fact]
    public void A_changed_return_type_moves_the_surface()
    {
        Assert.NotEqual(
            Surface("public class C { public int Value() => 1; }"),
            Surface("public class C { public long Value() => 1; }"));
    }

    [Fact]
    public void A_changed_const_value_moves_the_surface()
    {
        // The compiler inlines a const into whoever reads it, so a dependent built against the old
        // value is wrong until it is rebuilt - the value is part of the surface, not of the body.
        Assert.NotEqual(
            Surface("public class C { public const int Limit = 1; }"),
            Surface("public class C { public const int Limit = 2; }"));
    }

    [Fact]
    public void A_member_becoming_virtual_moves_the_surface()
    {
        // The graph carries virtual-to-override edges, so the modifier decides what a change to
        // this member is allowed to reach.
        Assert.NotEqual(
            Surface("public class C { public int Value() => 1; }"),
            Surface("public class C { public virtual int Value() => 1; }"));
    }

    [Fact]
    public void A_narrowed_accessibility_moves_the_surface()
    {
        Assert.NotEqual(
            Surface("public class C { public int Value() => 1; }"),
            Surface("public class C { internal int Value() => 1; }"));
    }

    [Fact]
    public void A_new_base_type_moves_the_surface()
    {
        Assert.NotEqual(
            Surface("public class B { } public class C { }"),
            Surface("public class B { } public class C : B { }"));
    }

    [Fact]
    public void A_new_implemented_interface_moves_the_surface()
    {
        Assert.NotEqual(
            Surface("public interface IThing { } public class C { }"),
            Surface("public interface IThing { } public class C : IThing { }"));
    }

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
    ];

    private static string Surface(string source) =>
        ProjectFingerprint.ComputeSurface(CSharpCompilation.Create(
            "SurfaceAsm",
            [CSharpSyntaxTree.ParseText(source, path: "/repo/Source.cs")],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
}
