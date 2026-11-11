using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// Source generators the workspace could not load. Built here against a Roslyn from the future,
/// which is what the Razor generator in SDK 10.0.403 is to a tool carrying 5.6: refused without a
/// word, so what it would generate was missing from the graph.
/// </summary>
internal static class FutureGenerator
{
    private const string Generator = """
        [Microsoft.CodeAnalysis.Generator]
        public sealed class FromTheFuture { }
        """;

    /// <summary>Emits a generator assembly referencing <c>Microsoft.CodeAnalysis</c> 99.0.</summary>
    public static string EmitFuture(string directory) =>
        Emit(directory, "Future.Generator", Stub("99.0.0.0"));

    /// <summary>Emits one referencing the Roslyn this tool carries.</summary>
    public static string EmitCurrent(string directory) =>
        Emit(directory, "Current.Generator", MetadataReference.CreateFromFile(typeof(Compilation).Assembly.Location));

    private static MetadataReference Stub(string version) =>
        CSharpCompilation.Create("Microsoft.CodeAnalysis",
                [CSharpSyntaxTree.ParseText($$"""
                    [assembly: System.Reflection.AssemblyVersion("{{version}}")]
                    namespace Microsoft.CodeAnalysis
                    {
                        public sealed class GeneratorAttribute : System.Attribute { }
                    }
                    """)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .ToMetadataReference();

    private static string Emit(string directory, string name, MetadataReference roslyn)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");

        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(Generator)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), roslyn],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }
}

public sealed class UnloadedGeneratorInspectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tia-generators-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A loaded assembly can hold its file open until the process exits.
        }
    }

    [Fact]
    public void A_generator_built_for_a_newer_compiler_is_reported_with_both_versions()
    {
        var reference = new AnalyzerFileReference(FutureGenerator.EmitFuture(_directory), new DefaultLoader());

        var unloaded = UnloadedGenerators.Inspect(reference);

        Assert.NotNull(unloaded);
        Assert.Equal("Future.Generator.dll", unloaded.Assembly);
        Assert.Contains("Roslyn 99.0", unloaded.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// An assembly with no generator in it is an analyzer, which changes nothing about what is
    /// compiled, and is not reported however it loads.
    /// </summary>
    [Fact]
    public void An_assembly_without_a_generator_is_not_reported()
    {
        var reference = new AnalyzerFileReference(typeof(UnloadedGeneratorInspectionTests).Assembly.Location, new DefaultLoader());

        Assert.Null(UnloadedGenerators.Inspect(reference));
    }

    /// <summary>
    /// An assembly that cannot be read cannot be said to declare no generator. It used to count
    /// as declaring none, so a generator whose file was missing or locked was the one case that
    /// went unreported: nothing loaded, nothing was expected, and its output was simply absent.
    /// </summary>
    [Fact]
    public void An_assembly_that_cannot_be_read_is_reported_rather_than_taken_to_declare_nothing()
    {
        var reference = new AnalyzerFileReference(Path.Combine(_directory, "Missing.Generator.dll"), new DefaultLoader());

        var unloaded = UnloadedGenerators.Inspect(reference);

        Assert.NotNull(unloaded);
        Assert.Equal("Missing.Generator.dll", unloaded.Assembly);
        Assert.Contains("could not be read", unloaded.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure is not remembered. A file that could not be read while a build was writing it
    /// is read again the next time, which matters to a resident process that asks more than once.
    /// </summary>
    [Fact]
    public void An_assembly_that_becomes_readable_is_read_again()
    {
        var path = Path.Combine(_directory, "Current.Generator.dll");
        var reference = new AnalyzerFileReference(path, new DefaultLoader());
        Assert.NotNull(UnloadedGenerators.Inspect(reference));

        FutureGenerator.EmitFuture(_directory);
        File.Move(Path.Combine(_directory, "Future.Generator.dll"), path);

        var unloaded = UnloadedGenerators.Inspect(new AnalyzerFileReference(path, new DefaultLoader()));

        Assert.NotNull(unloaded);
        Assert.Contains("Roslyn 99.0", unloaded.Reason, StringComparison.Ordinal);
    }

    private sealed class DefaultLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath) => System.Reflection.Assembly.LoadFrom(fullPath);
    }
}

/// <summary>The fixture solution with <c>Fixtures.Tests</c> using a generator that cannot load.</summary>
public sealed class UnloadedGeneratorFixture : IDisposable
{
    public XunitFixtureRepository Repository { get; } = new();

    public UnloadedGeneratorFixture()
    {
        FutureGenerator.EmitFuture(Repository.PathOf("generators"));

        var tests = Repository.PathOf("Fixtures.Tests", "Fixtures.Tests.csproj");
        File.WriteAllText(tests, File.ReadAllText(tests).Replace(
            "</Project>",
            "  <ItemGroup>\n    <Analyzer Include=\"../generators/Future.Generator.dll\" />\n  </ItemGroup>\n</Project>",
            StringComparison.Ordinal));

        TestGit.Run(Repository.Root, "add", "-A");
        TestGit.Run(Repository.Root, "commit", "-m", "a generator from the future");
    }

    public void Dispose() => Repository.Dispose();
}

public sealed class UnloadedGeneratorTests(UnloadedGeneratorFixture fixture) : IClassFixture<UnloadedGeneratorFixture>, IDisposable
{
    private XunitFixtureRepository Repository => fixture.Repository;

    public void Dispose()
    {
        Repository.Revert();
        TestGit.Run(Repository.Root, "reset", "-q", "--hard");
    }

    private static bool IsUnloadedGenerator(Tia.Core.Reporting.WideningEvent widening) =>
        widening is { Cause: "SourceGenerator", Scope: "Fixtures.Tests" } &&
        widening.Detail.Contains("Future.Generator.dll", StringComparison.Ordinal) &&
        widening.Detail.Contains("Roslyn 99.0", StringComparison.Ordinal);

    [Fact]
    public async Task A_change_beneath_the_project_widens_it()
    {
        Repository.Edit("Fixtures.Core/Calculator.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => b + a;");

        var report = await Repository.AnalyzeAsync();

        var tests = report.Projects.Single(p => p.Name == "Fixtures.Tests");
        Assert.Contains(report.Widenings, IsUnloadedGenerator);
        Assert.Equal(tests.TotalTests, tests.SelectedTests);
    }

    [Fact]
    public async Task A_change_the_project_does_not_reach_leaves_it_alone()
    {
        Repository.Edit("Fixtures.NUnitTests/CounterTests.cs", "Is.EqualTo(-1)", "Is.EqualTo(-1).Within(0)");

        var report = await Repository.AnalyzeAsync();

        Assert.DoesNotContain(report.Widenings, IsUnloadedGenerator);
    }
}
