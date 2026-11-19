using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// C# 15 unions call a constructor on every conversion and read <c>Value</c> on every match, and
/// the source names neither (#216). Roslyn 5.12 binds them, so a solution that declares one is now
/// analysed selectively rather than failing to compile - these edges are what keeps that safe.
/// </summary>
public class UnionTests
{
    private const string Declaration = """
        namespace System.Runtime.CompilerServices
        {
            [System.AttributeUsage(System.AttributeTargets.All)] public sealed class UnionAttribute : System.Attribute { }
            public interface IUnion { object? Value { get; } }
        }

        namespace App
        {
            public record Ok;
            public record Err;

            [System.Runtime.CompilerServices.Union]
            public struct Result : System.Runtime.CompilerServices.IUnion
            {
                private readonly object? _value;
                public Result(Ok ok) { _value = ok; }
                public Result(Err err) { _value = err; }
                public object? Value => _value;
            }

            public static class Factory
            {
                // Each constructor has a caller outside its type, so the "no outside caller" fallback
                // that reaches an otherwise unreferenced overload cannot be what finds the test.
                public static Result MakeOk() => new Result(new Ok());
                public static Result MakeErr() => new Result(new Err());
            }
        }
        """;

    private static CSharpCompilation Compile(string use)
    {
        var compilation = CompilationHarness.Compile(
            "TestAsm",
            new CSharpParseOptions(LanguageVersion.Preview),
            ("/repo/Test.cs", Declaration + "\nnamespace App { public static class Site { " + use + " } }"));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    private static string ConstructorKey(Compilation compilation, string parameterType) =>
        Tia.Core.Model.SymbolKeys.For(compilation.GetTypeByMetadataName("App.Result")!
            .Constructors.Single(c => c.Parameters is [{ } p] && p.Type.Name == parameterType))!;

    [Theory]
    [InlineData("argument", "Ok", "public static string Use() => Take(new Ok()); static string Take(Result r) => \"\";")]
    [InlineData("return", "Err", "public static Result Use() => new Err();")]
    [InlineData("initialiser", "Ok", "public static object Use() { Result r = new Ok(); return r; }")]
    [InlineData("foreach", "Ok", "public static int Use() { var n = 0; foreach (Result r in new Ok[] { new Ok() }) { n++; } return n; }")]
    public void A_union_conversion_reaches_the_constructor_it_calls(string context, string parameter, string use)
    {
        var compilation = Compile(use);
        var graph = CompilationHarness.BuildGraph(compilation);

        var constructor = ConstructorKey(compilation, parameter);
        var user = CompilationHarness.KeyOf(compilation, "App.Site", "Use");

        Assert.True(new ImpactSelector().Traverse(graph, [constructor]).Impacted.Contains(user), context);
    }

    [Theory]
    [InlineData("switch expression", "public static string Use(Result r) => r switch { Ok => \"ok\", _ => \"other\" };")]
    [InlineData("is pattern", "public static bool Use(Result r) => r is Ok;")]
    [InlineData("switch statement", "public static string Use(Result r) { switch (r) { case Ok: return \"ok\"; default: return \"\"; } }")]
    [InlineData("declaration pattern", "public static bool Use(Result r) => r is Ok o;")]
    [InlineData("nullable union", "public static bool Use(Result? r) => r is Ok;")]
    [InlineData("nested pattern", "public record Box(Result R); public static bool Use(Box b) => b is { R: Ok };")]
    public void Matching_a_union_reaches_its_Value(string context, string use)
    {
        var compilation = Compile(use);
        var graph = CompilationHarness.BuildGraph(compilation);

        var value = CompilationHarness.KeyOf(compilation, "App.Result", "Value");
        var user = CompilationHarness.KeyOf(compilation, "App.Site", "Use");

        Assert.True(new ImpactSelector().Traverse(graph, [value]).Impacted.Contains(user), context);
    }

    private static readonly string Prelude = Declaration[..Declaration.IndexOf("namespace App", StringComparison.Ordinal)];

    private static CSharpCompilation CompileWith(string declaration, string use)
    {
        var compilation = CompilationHarness.Compile(
            "TestAsm",
            new CSharpParseOptions(LanguageVersion.Preview),
            ("/repo/Test.cs", Prelude + declaration + "\nnamespace App { public static class Site { " + use + " } }"));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    private static bool Reaches(CSharpCompilation compilation, string seed, string user) =>
        new ImpactSelector().Traverse(CompilationHarness.BuildGraph(compilation), [seed]).Impacted.Contains(user);

    /// <summary>A union built through a member provider converts by a static factory, not a constructor.</summary>
    [Fact]
    public void A_union_conversion_reaches_the_factory_of_a_member_provider()
    {
        var compilation = CompileWith("""
            namespace App
            {
                public record Ok;
                public record Err;

                [System.Runtime.CompilerServices.Union]
                public class Result : Result.IUnionMembers
                {
                    private readonly object? _v;
                    private Result(object? v) { _v = v; }
                    public interface IUnionMembers
                    {
                        public static Result Create(Ok o) => new Result(o);
                        public static Result Create(Err e) => new Result(e);
                        object? Value { get; }
                    }
                    object? IUnionMembers.Value => _v;
                }
            }
            """, "public static Result Use() => new Ok();");

        var provider = compilation.GetTypeByMetadataName("App.Result+IUnionMembers")!;
        var create = SymbolKeysFor(provider.GetMembers("Create").Cast<IMethodSymbol>().Single(m => m.Parameters[0].Type.Name == "Ok"));

        Assert.True(Reaches(compilation, create, CompilationHarness.KeyOf(compilation, "App.Site", "Use")));
    }

    [Fact]
    public void Matching_a_union_reaches_a_Value_it_inherits()
    {
        var compilation = CompileWith("""
            namespace App
            {
                public record Ok;
                public record Err;

                public abstract class UBase : System.Runtime.CompilerServices.IUnion
                {
                    protected object? _v;
                    public object? Value => _v;
                }

                [System.Runtime.CompilerServices.Union]
                public class Result : UBase
                {
                    public Result(Ok o) { _v = o; }
                    public Result(Err e) { _v = e; }
                }
            }
            """, "public static bool Use(Result r) => r is Ok;");

        Assert.True(Reaches(compilation, CompilationHarness.KeyOf(compilation, "App.UBase", "Value"), CompilationHarness.KeyOf(compilation, "App.Site", "Use")));
    }

    [Fact]
    public void Matching_a_union_reaches_the_Value_a_member_provider_implements()
    {
        var compilation = CompileWith("""
            namespace App
            {
                public record Ok;
                public record Err;

                [System.Runtime.CompilerServices.Union]
                public class Result : Result.IUnionMembers
                {
                    private readonly object? _v;
                    private Result(object? v) { _v = v; }
                    public interface IUnionMembers
                    {
                        public static Result Create(Ok o) => new Result(o);
                        public static Result Create(Err e) => new Result(e);
                        object? Value { get; }
                    }
                    object? IUnionMembers.Value => _v;
                }
            }
            """, "public static bool Use(Result r) => r is Ok;");

        var implementation = CompilationHarness.KeyOf(compilation, "App.Result", "App.Result.IUnionMembers.Value");

        Assert.True(Reaches(compilation, implementation, CompilationHarness.KeyOf(compilation, "App.Site", "Use")));
    }

    /// <summary>The keyword form's attribute is synthesised, so it is recognised by its declaration.</summary>
    [Theory]
    [InlineData("HasValue", "public static bool Use(Result r) => r is Ok o;")]
    [InlineData("TryGetValue", "public static string Use(Result r) => r switch { Err => \"err\", _ => \"other\" };")]
    public void Matching_a_keyword_union_reaches_the_members_its_body_declares(string member, string use)
    {
        var compilation = CompileWith("""
            namespace App
            {
                public record Ok;
                public record Err;

                public union Result(Ok, Err)
                {
                    public bool HasValue => Value is not null;
                    public bool TryGetValue(out Ok? o) { o = Value as Ok; return o is not null; }
                    public bool TryGetValue(out Err? e) { e = Value as Err; return e is not null; }
                }
            }
            """, use);

        Assert.True(Reaches(compilation, CompilationHarness.KeyOf(compilation, "App.Result", member), CompilationHarness.KeyOf(compilation, "App.Site", "Use")), member);
    }

    private static string SymbolKeysFor(ISymbol symbol) => Tia.Core.Model.SymbolKeys.For(symbol)!;
}
