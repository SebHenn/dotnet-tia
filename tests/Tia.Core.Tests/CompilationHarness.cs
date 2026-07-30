using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Analysis;
using Tia.Core.Model;

namespace Tia.Core.Tests;

/// <summary>
/// In-memory compilations for the engine tests. Nothing here touches MSBuild or the SDK, which is
/// the whole reason Tia.Core is kept free of workspace dependencies.
/// </summary>
public static class CompilationHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    public static CSharpCompilation Compile(string assemblyName, params (string Path, string Source)[] files)
    {
        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.Path))
            .ToArray();

        return CSharpCompilation.Create(
            assemblyName,
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    public static CSharpCompilation Compile(string source, string assemblyName = "TestAsm", string path = "/repo/Test.cs") =>
        Compile(assemblyName, (path, source));

    /// <summary>Compiles and asserts the result has no errors, so a broken fixture fails loudly.</summary>
    public static CSharpCompilation CompileValid(string source, string assemblyName = "TestAsm", string path = "/repo/Test.cs")
    {
        var compilation = Compile(source, assemblyName, path);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
        return compilation;
    }

    public static ImpactGraph BuildGraph(Compilation compilation, string projectName = "TestProject")
    {
        var tracked = new HashSet<string>(StringComparer.Ordinal) { compilation.AssemblyName! };
        return new ReferenceGraphBuilder(tracked).Build(compilation, projectName);
    }

    public static ImpactGraph BuildGraph(IReadOnlyList<Compilation> compilations, string projectPrefix = "Project")
    {
        var tracked = new HashSet<string>(compilations.Select(c => c.AssemblyName!), StringComparer.Ordinal);
        var builder = new ReferenceGraphBuilder(tracked);
        var graph = new ImpactGraph();

        for (var i = 0; i < compilations.Count; i++)
        {
            graph.Merge(builder.Build(compilations[i], $"{projectPrefix}{i}"));
        }

        return graph;
    }

    /// <summary>Graph key of a symbol found by its display name, for readable assertions.</summary>
    public static string KeyOf(Compilation compilation, string metadataName, string? memberName = null)
    {
        var type = compilation.GetTypeByMetadataName(metadataName)
                   ?? throw new InvalidOperationException($"type '{metadataName}' was not found");

        if (memberName is null)
        {
            return SymbolKeys.For(type)!;
        }

        var member = type.GetMembers(memberName).FirstOrDefault()
                     ?? throw new InvalidOperationException($"member '{memberName}' was not found on '{metadataName}'");

        return SymbolKeys.For(member)!;
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var name in new[] { "System.Private.CoreLib", "System.Runtime", "System.Console", "System.Linq", "System.Collections", "netstandard" })
        {
            var path = Path.Combine(directory, name + ".dll");
            if (File.Exists(path))
            {
                builder.Add(MetadataReference.CreateFromFile(path));
            }
        }

        builder.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return builder.ToImmutable();
    }

    /// <summary>Minimal stand-ins for the test frameworks, so discovery can be exercised without
    /// taking a reference on all four of them.</summary>
    public const string XunitShim = """
        namespace Xunit
        {
            public class FactAttribute : System.Attribute { }
            public class TheoryAttribute : FactAttribute { }
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
            public class InlineDataAttribute : System.Attribute { public InlineDataAttribute(params object[] data) { } }
            public interface IClassFixture<T> { }
        }
        """;

    public const string NUnitShim = """
        namespace NUnit.Framework
        {
            public class TestAttribute : System.Attribute { }
            public class TestCaseAttribute : System.Attribute { public TestCaseAttribute(params object[] data) { } }
            public class SetUpAttribute : System.Attribute { }
            public class OneTimeSetUpAttribute : System.Attribute { }
        }
        """;
}
