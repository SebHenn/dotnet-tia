using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Tia.Core.Model;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// The invalidation half of the graph cache.
/// </summary>
/// <remarks>
/// <para>
/// A project's content hash decides whether its cached graph fragment is reused verbatim. Every
/// input this hash forgets is a stale fragment merged into the graph as if it were current - the
/// same silent-miss shape the surface hash had, and just as invisible, because a wrong answer from
/// a reused fragment looks exactly like a right one. The method's doc comment claims
/// AdditionalFiles, .editorconfig, metadata references and analyzer references are all folded in;
/// nothing verified any of it.
/// </para>
/// <para>
/// These build projects through <see cref="AdhocWorkspace"/> rather than MSBuild, which is also
/// how the "no compilation is produced" claim gets pinned: the contexts below carry a
/// <see cref="Lazy{T}"/> that throws if anything touches it.
/// </para>
/// </remarks>
public sealed class ContentHashTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tia-content-hash").FullName;

    [Fact]
    public void The_same_project_hashes_the_same_twice()
    {
        Assert.Equal(Hash(Project()), Hash(Project()));
    }

    [Fact]
    public void A_source_change_moves_the_content_hash()
    {
        Assert.NotEqual(
            Hash(Project(sources: [("/repo/A.cs", "class A { }")])),
            Hash(Project(sources: [("/repo/A.cs", "class A { int x; }")])));
    }

    [Fact]
    public void Renaming_a_source_file_moves_the_content_hash()
    {
        // Paths are how the graph attributes a change back to a project, so identical text under a
        // different name is not the same project.
        Assert.NotEqual(
            Hash(Project(sources: [("/repo/A.cs", "class A { }")])),
            Hash(Project(sources: [("/repo/B.cs", "class A { }")])));
    }

    [Fact]
    public void The_order_documents_arrive_in_does_not_move_the_content_hash()
    {
        // MSBuild does not promise an order and a reordered globbing result is not a change.
        Assert.Equal(
            Hash(Project(sources: [("/repo/A.cs", "class A { }"), ("/repo/B.cs", "class B { }")])),
            Hash(Project(sources: [("/repo/B.cs", "class B { }"), ("/repo/A.cs", "class A { }")])));
    }

    [Fact]
    public void A_project_file_change_moves_the_content_hash()
    {
        var first = Project();
        var hash = Hash(first);

        File.WriteAllText(first.Descriptor.FilePath, "<Project><PropertyGroup><LangVersion>13</LangVersion></PropertyGroup></Project>");

        Assert.NotEqual(hash, Hash(first));
    }

    [Fact]
    public void An_additional_file_change_moves_the_content_hash()
    {
        // A source generator reads these. A fragment that ignored them would be reused unchanged
        // after the generated code underneath it moved.
        Assert.NotEqual(
            Hash(Project(additional: [("/repo/strings.resx", "<root />")])),
            Hash(Project(additional: [("/repo/strings.resx", "<root><data /></root>")])));
    }

    [Fact]
    public void An_editorconfig_change_moves_the_content_hash()
    {
        Assert.NotEqual(
            Hash(Project(config: [("/repo/.editorconfig", "root = true")])),
            Hash(Project(config: [("/repo/.editorconfig", "root = true\nbuild_property.X = 1")])));
    }

    [Fact]
    public void A_new_metadata_reference_moves_the_content_hash()
    {
        // Upgrading a package is a change to what this project binds against, and it touches no
        // file inside the project.
        Assert.NotEqual(
            Hash(Project(references: [])),
            Hash(Project(references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])));
    }

    [Fact]
    public void A_new_analyzer_reference_moves_the_content_hash()
    {
        // Same argument, for the package that emits the code rather than the one being called.
        Assert.NotEqual(
            Hash(Project(analyzers: [])),
            Hash(Project(analyzers: [Analyzer("/packages/Generator.dll")])));
    }

    [Fact]
    public void Upgrading_an_analyzer_moves_the_content_hash()
    {
        Assert.NotEqual(
            Hash(Project(analyzers: [Analyzer("/packages/generator/1.0.0/Generator.dll")])),
            Hash(Project(analyzers: [Analyzer("/packages/generator/1.1.0/Generator.dll")])));
    }

    [Fact]
    public void Every_project_is_hashed_and_keyed_by_name()
    {
        var hashes = ProjectFingerprint.ComputeContentHashes(
            [Project("App"), Project("App.Tests", sources: [("/repo/T.cs", "class T { }")])],
            TestContext.Current.CancellationToken);

        Assert.Equal(["App", "App.Tests"], hashes.Keys.Order(StringComparer.Ordinal));
        Assert.NotEqual(hashes["App"], hashes["App.Tests"]);
    }

    [Fact]
    public void Hashing_content_never_produces_a_compilation()
    {
        // This is the reason the method exists rather than being a property of the compilation:
        // parsing every document is most of the cost of loading a solution, and a project whose
        // content is unchanged must never be parsed at all. The lazies below throw if touched.
        var exception = Record.Exception(
            () => ProjectFingerprint.ComputeContentHashes([Project()], TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public void An_unreadable_project_file_forces_a_rebuild_rather_than_pinning_the_hash()
    {
        // A constant for "could not read" is a stable hash: the failed read compares equal to the
        // last failed read, and the value is stored with the fragment, so one transient IO error
        // pins the cache to something no successful read can reproduce. Two attempts must differ.
        var missing = Project() with
        {
            Descriptor = Descriptor("App", Path.Combine(_root, "does-not-exist.csproj")),
        };

        Assert.NotEqual(Hash(missing), Hash(missing));
    }

    [Fact]
    public void A_project_path_that_is_a_directory_is_handled_like_any_other_read_failure()
    {
        // File.ReadAllText on a directory throws UnauthorizedAccessException, not IOException, so
        // this used to escape the guard entirely and take down the whole analysis.
        var directory = Project() with { Descriptor = Descriptor("App", _root) };

        Assert.NotEqual(Hash(directory), Hash(directory));
    }

    private static string Hash(ProjectContext context) =>
        ProjectFingerprint.ComputeContentHashes([context], TestContext.Current.CancellationToken)[context.Name];

    private ProjectContext Project(
        string name = "App",
        IReadOnlyList<(string Path, string Text)>? sources = null,
        IReadOnlyList<(string Path, string Text)>? additional = null,
        IReadOnlyList<(string Path, string Text)>? config = null,
        IReadOnlyList<MetadataReference>? references = null,
        IReadOnlyList<AnalyzerReference>? analyzers = null)
    {
        var projectPath = Path.Combine(_root, name + ".csproj");
        if (!File.Exists(projectPath))
        {
            File.WriteAllText(projectPath, "<Project />");
        }

        var id = ProjectId.CreateNewId(name);
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            id,
            VersionStamp.Default,
            name,
            assemblyName: name,
            LanguageNames.CSharp,
            filePath: projectPath,
            metadataReferences: references ?? [],
            analyzerReferences: analyzers ?? []));

        foreach (var (path, text) in sources ?? [("/repo/Source.cs", "class Source { }")])
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(id), Path.GetFileName(path), SourceText.From(text), filePath: path);
        }

        foreach (var (path, text) in additional ?? [])
        {
            solution = solution.AddAdditionalDocument(
                DocumentId.CreateNewId(id), Path.GetFileName(path), SourceText.From(text), filePath: path);
        }

        foreach (var (path, text) in config ?? [])
        {
            solution = solution.AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(id), Path.GetFileName(path), SourceText.From(text), filePath: path);
        }

        return new ProjectContext
        {
            Project = solution.GetProject(id)!,
            Descriptor = Descriptor(name, projectPath),
            LazyCompilation = new Lazy<Compilation>(
                () => throw new InvalidOperationException("content hashing must not need a compilation")),
            LazyGeneratedOutput = new Lazy<GeneratedOutput>(
                () => throw new InvalidOperationException("content hashing must not need generated output")),
        };
    }

    private static ProjectDescriptor Descriptor(string name, string path) => new()
    {
        Name = name,
        AssemblyName = name,
        FilePath = path,
        ProjectReferences = [],
    };

    private static AnalyzerReference Analyzer(string path) =>
        new AnalyzerFileReference(Path.GetFullPath(path), NullAssemblyLoader.Instance);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Never asked to load anything: only <c>FullPath</c> reaches the hash.</summary>
    private sealed class NullAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static readonly NullAssemblyLoader Instance = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => throw new NotSupportedException();
    }
}
