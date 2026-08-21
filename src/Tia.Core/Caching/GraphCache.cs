using System.Security.Cryptography;
using System.Text;
using Tia.Core.Model;

namespace Tia.Core.Caching;

/// <summary>
/// One reflecting or serializing site, and the member that holds it.
/// </summary>
/// <param name="OwningMemberKey">
/// Null when the site sits outside any member - a field initialiser, a top-level statement - which
/// leaves nothing to seed and forces the project-wide reading instead.
/// </param>
public sealed record ReflectionRecord(string Description, string? OwningMemberKey, string FilePath);

/// <summary>
/// One mention of an HTTP route, and the member it sits in.
/// </summary>
/// <param name="Template">
/// The normalised route. For an endpoint this is its template with parameter segments replaced by
/// <c>*</c>; for a reference it is the literal path a caller asked for. Matching the two is
/// <c>RouteSeeder</c>'s job, because a reference to <c>contributors/7</c> has to meet a template of
/// <c>contributors/*</c> and neither side can know that alone.
/// </param>
/// <param name="IsEndpoint">
/// True when this member *serves* the route - collected positionally, from a <c>Map*</c> call's
/// route argument or a routing attribute. False when it merely names the string. The distinction is
/// the whole difference between this and joining any two members that share a literal: references
/// never join to each other, only to a template something actually declared.
/// </param>
/// <param name="ProjectName">Which project declared it, so a widening can name the right one.</param>
/// <param name="FilePath">
/// Where the route text sits, with <paramref name="Line"/>. A change to the template itself is the
/// one case this edge cannot carry: the graph is built from the new source, so the endpoint's new
/// template no longer matches the old path its callers still name, and the edge disappears exactly
/// when it is needed. Knowing where the text lives lets that case widen instead of vanishing.
/// </param>
public sealed record RouteRecord(
    string Template,
    string OwningMemberKey,
    bool IsEndpoint,
    string ProjectName = "",
    string FilePath = "",
    int Line = 0);

/// <summary>
/// What one member can obtain an instance of, before the cross-project join.
/// </summary>
/// <param name="ObtainedTypeKeys">
/// Concrete types the member can hold directly, closed over their base types. Only what this
/// project's own source shows; what it can obtain <i>through</i> the members it calls is the
/// fixpoint's job, and that cannot be decided one project at a time.
/// </param>
/// <param name="IsUnknown">
/// The member can hold something the analysis cannot name - it reflects, it uses <c>dynamic</c>, or
/// it takes an instance of ours back from code outside the graph. An unknown member permits every
/// hop, which is the sound answer and the opposite of what an empty set would say.
/// </param>
public sealed record TypeFlowFact(string MemberKey, IReadOnlyList<string> ObtainedTypeKeys, bool IsUnknown);

/// <summary>
/// A changed file that did not bind, and the first error it produced.
/// </summary>
/// <remarks>
/// Recorded per file rather than per project because the project-level verdict deliberately reads
/// declarations only: a broken method body does not stop a project's declarations from being what
/// every other project binds against, so it must not condemn the whole project - but it does mean
/// this file's edges are unreliable, and a diff that touches it cannot be trusted to a symbol.
/// Stored so that deciding this costs a lookup rather than a compilation.
/// </remarks>
public sealed record FileCompileError(string FilePath, string Error);

/// <summary>One project's slice of the graph, keyed by a fingerprint of everything that produced it.</summary>
public sealed record ProjectGraphFragment
{
    public required string ProjectName { get; init; }

    public required string Fingerprint { get; init; }

    /// <summary>Hash of the project's own source. Lets a rerun tell "unchanged" from "changed"
    /// without producing a compilation.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>Hash of the declarations a referencing project binds against.</summary>
    public string SurfaceHash { get; init; } = string.Empty;

    /// <summary>Whether the project's declarations bound cleanly when the fragment was built.
    /// Same inputs, same verdict - so an unchanged project need not be re-checked.</summary>
    public string? CompileError { get; init; }

    /// <summary>
    /// Every reflecting or serializing member in the project. Stored with the fragment because a
    /// reflecting member has to be seeded whether or not anything reaches it - see
    /// <see cref="ReflectionRecord"/> - which means the whole solution has to be scanned on every
    /// run, and re-scanning a project whose content has not moved would put the cost of compiling
    /// the entire solution back into a run that otherwise compiles nothing.
    /// </summary>
    public IReadOnlyList<ReflectionRecord> Reflections { get; init; } = [];

    /// <summary>
    /// Every HTTP route this project mentions, either as an endpoint or as a caller naming one.
    /// Stored with the fragment for the same reason as <see cref="Reflections"/>: the join is
    /// cross-project and happens after the merge, but finding the mentions needs a compilation, and
    /// re-scanning a project whose content has not moved would put the cost of compiling the whole
    /// solution back into a run that otherwise compiles nothing.
    /// </summary>
    public IReadOnlyList<RouteRecord> Routes { get; init; } = [];

    /// <summary>
    /// What each of the project's members can obtain an instance of. Empty unless the run asked for
    /// type flow, which is why the flag is part of the cache file's key: a fragment built without
    /// it carries no facts, and reusing one under <c>--type-flow</c> would silently draw the bound
    /// from an empty set - the shape of a missed test rather than a slow run.
    /// </summary>
    public IReadOnlyList<TypeFlowFact> TypeFacts { get; init; } = [];

    /// <summary>
    /// Where every declaration in the project sits. This is what lets a changed line range be
    /// mapped onto the symbol it declares without producing a compilation, which on a warm run was
    /// the largest single cost left - see <see cref="DeclarationSite"/>.
    /// </summary>
    public IReadOnlyList<DeclarationSite> Declarations { get; init; } = [];

    /// <summary>Files whose bodies did not bind when the fragment was built.</summary>
    public IReadOnlyList<FileCompileError> FileErrors { get; init; } = [];

    /// <summary>
    /// How many documents the project's source generators emitted. Stored because asking costs a
    /// compilation, and two decisions need the answer on every run: whether a comment-only change
    /// can be dismissed (a generator reads trivia, so there it cannot) and whether generated code
    /// has to be seeded at all.
    /// </summary>
    public int GeneratedDocumentCount { get; init; }

    public required ImpactGraph Graph { get; init; }

    public required IReadOnlyList<TestMethod> Tests { get; init; }
}

/// <summary>
/// The on-disk graph cache.
/// </summary>
/// <remarks>
/// Invalidation is per project. A project is invalidated by a change to its own content, or by a
/// change to the *declaration surface* of anything it references - not by any change at all over
/// there. Its edges come from binding its syntax against those declarations, and nothing in them
/// depends on a dependency's method bodies, so a body-only edit upstream leaves this fragment
/// valid. Folding whole dependency fingerprints in instead was correct and useless; see
/// <c>ProjectFingerprint</c> for the measurement that killed it. Cold graph construction is the
/// main performance risk on a large solution and this is what keeps a warm run cheap.
/// </remarks>
public sealed class GraphCache
{
    private const uint Magic = 0x47414954; // "TIAG"
    private const int FormatVersion = 8;

    public required string SdkVersion { get; init; }

    public required Dictionary<string, ProjectGraphFragment> Projects { get; init; }

    public static GraphCache Empty(string sdkVersion) => new()
    {
        SdkVersion = sdkVersion,
        Projects = new Dictionary<string, ProjectGraphFragment>(StringComparer.Ordinal),
    };

    /// <summary>
    /// Cache file name for a solution. The key isolates unrelated solutions, SDKs, and runs that
    /// asked for different analyses.
    /// </summary>
    /// <remarks>
    /// <paramref name="typeFlow"/> is part of the key rather than a field to check on load because
    /// the two fragments are not interchangeable in both directions. One built without type facts
    /// is missing them, and a run that wanted them would draw its bound from an empty set and call
    /// that precision. Two files is the cheap way to make that unrepresentable.
    /// </remarks>
    public static string FileName(string solutionPath, string sdkVersion, bool typeFlow = false) =>
        $"graph-{ShortHash(solutionPath + "|" + sdkVersion + (typeFlow ? "|type-flow" : string.Empty))}.bin";

    public static GraphCache? TryLoad(string path, string expectedSdkVersion)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            var strings = ReadStringTable(reader);
            var sdkVersion = strings[reader.ReadInt32()];
            if (!string.Equals(sdkVersion, expectedSdkVersion, StringComparison.Ordinal))
            {
                return null;
            }

            var projects = new Dictionary<string, ProjectGraphFragment>(StringComparer.Ordinal);
            var projectCount = reader.ReadInt32();

            for (var i = 0; i < projectCount; i++)
            {
                var name = strings[reader.ReadInt32()];
                var fingerprint = strings[reader.ReadInt32()];
                var contentHash = strings[reader.ReadInt32()];
                var surfaceHash = strings[reader.ReadInt32()];
                var compileError = ReadNullable(reader, strings);
                var graph = new ImpactGraph();

                var nodeCount = reader.ReadInt32();
                for (var n = 0; n < nodeCount; n++)
                {
                    graph.AddNode(new SymbolNode
                    {
                        Key = strings[reader.ReadInt32()],
                        DisplayName = strings[reader.ReadInt32()],
                        Kind = (SymbolNodeKind)reader.ReadByte(),
                        ProjectName = strings[reader.ReadInt32()],
                        ContainingTypeKey = ReadNullable(reader, strings),
                        FilePath = ReadNullable(reader, strings),
                    });
                }

                var edgeCount = reader.ReadInt32();
                for (var e = 0; e < edgeCount; e++)
                {
                    var from = strings[reader.ReadInt32()];
                    var to = strings[reader.ReadInt32()];
                    graph.AddEdge(from, to, (EdgeKind)reader.ReadInt32());
                }

                var testCount = reader.ReadInt32();
                var tests = new List<TestMethod>(testCount);
                for (var t = 0; t < testCount; t++)
                {
                    tests.Add(new TestMethod
                    {
                        SymbolKey = strings[reader.ReadInt32()],
                        ClassKey = strings[reader.ReadInt32()],
                        Namespace = strings[reader.ReadInt32()],
                        ClassName = strings[reader.ReadInt32()],
                        MethodName = strings[reader.ReadInt32()],
                        ProjectName = strings[reader.ReadInt32()],
                        Framework = (TestFramework)reader.ReadByte(),
                        IsParameterized = reader.ReadBoolean(),
                    });
                }

                var reflectionCount = reader.ReadInt32();
                var reflections = new List<ReflectionRecord>(reflectionCount);
                for (var r = 0; r < reflectionCount; r++)
                {
                    reflections.Add(new ReflectionRecord(
                        strings[reader.ReadInt32()],
                        ReadNullable(reader, strings),
                        strings[reader.ReadInt32()]));
                }

                var routeCount = reader.ReadInt32();
                var routes = new List<RouteRecord>(routeCount);
                for (var r = 0; r < routeCount; r++)
                {
                    routes.Add(new RouteRecord(
                        strings[reader.ReadInt32()],
                        strings[reader.ReadInt32()],
                        reader.ReadBoolean(),
                        strings[reader.ReadInt32()],
                        strings[reader.ReadInt32()],
                        reader.ReadInt32()));
                }

                var factCount = reader.ReadInt32();
                var typeFacts = new List<TypeFlowFact>(factCount);
                for (var f = 0; f < factCount; f++)
                {
                    var memberKey = strings[reader.ReadInt32()];
                    var obtainedCount = reader.ReadInt32();
                    var obtained = new string[obtainedCount];
                    for (var o = 0; o < obtainedCount; o++)
                    {
                        obtained[o] = strings[reader.ReadInt32()];
                    }

                    typeFacts.Add(new TypeFlowFact(memberKey, obtained, reader.ReadBoolean()));
                }

                var declarationCount = reader.ReadInt32();
                var declarations = new List<DeclarationSite>(declarationCount);
                for (var d = 0; d < declarationCount; d++)
                {
                    declarations.Add(new DeclarationSite
                    {
                        ProjectName = strings[reader.ReadInt32()],
                        FilePath = strings[reader.ReadInt32()],
                        Key = strings[reader.ReadInt32()],
                        StartLine = reader.ReadInt32(),
                        EndLine = reader.ReadInt32(),
                        IsType = reader.ReadBoolean(),
                        IsInlined = reader.ReadBoolean(),
                    });
                }

                var fileErrorCount = reader.ReadInt32();
                var fileErrors = new List<FileCompileError>(fileErrorCount);
                for (var f = 0; f < fileErrorCount; f++)
                {
                    fileErrors.Add(new FileCompileError(strings[reader.ReadInt32()], strings[reader.ReadInt32()]));
                }

                // Read before the initializer, not inside it. Every field here comes off a stream
                // in a fixed order, and an initializer that reads is one reordering away from
                // decoding a different file than the one that was written.
                var generatedDocumentCount = reader.ReadInt32();

                projects[name] = new ProjectGraphFragment
                {
                    ProjectName = name,
                    Fingerprint = fingerprint,
                    ContentHash = contentHash,
                    SurfaceHash = surfaceHash,
                    CompileError = compileError,
                    Reflections = reflections,
                    Routes = routes,
                    TypeFacts = typeFacts,
                    Declarations = declarations,
                    FileErrors = fileErrors,
                    GeneratedDocumentCount = generatedDocumentCount,
                    Graph = graph,
                    Tests = tests,
                };
            }

            return new GraphCache { SdkVersion = sdkVersion, Projects = projects };
        }
        catch (Exception)
        {
            // A truncated or stale cache is never a reason to fail: rebuild from scratch.
            return null;
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var table = new StringTable();
        table.Add(SdkVersion);

        foreach (var fragment in Projects.Values)
        {
            table.Add(fragment.ProjectName);
            table.Add(fragment.Fingerprint);
            table.Add(fragment.ContentHash);
            table.Add(fragment.SurfaceHash);
            table.AddNullable(fragment.CompileError);

            foreach (var node in fragment.Graph.Nodes.Values)
            {
                table.Add(node.Key);
                table.Add(node.DisplayName);
                table.Add(node.ProjectName);
                table.AddNullable(node.ContainingTypeKey);
                table.AddNullable(node.FilePath);
            }

            foreach (var (from, to, _) in fragment.Graph.Edges)
            {
                table.Add(from);
                table.Add(to);
            }

            foreach (var test in fragment.Tests)
            {
                table.Add(test.SymbolKey);
                table.Add(test.ClassKey);
                table.Add(test.Namespace);
                table.Add(test.ClassName);
                table.Add(test.MethodName);
                table.Add(test.ProjectName);
            }

            foreach (var reflection in fragment.Reflections)
            {
                table.Add(reflection.Description);
                table.AddNullable(reflection.OwningMemberKey);
                table.Add(reflection.FilePath);
            }

            foreach (var route in fragment.Routes)
            {
                table.Add(route.Template);
                table.Add(route.OwningMemberKey);
                table.Add(route.ProjectName);
                table.Add(route.FilePath);
            }

            foreach (var fact in fragment.TypeFacts)
            {
                table.Add(fact.MemberKey);
                foreach (var typeKey in fact.ObtainedTypeKeys)
                {
                    table.Add(typeKey);
                }
            }

            foreach (var declaration in fragment.Declarations)
            {
                table.Add(declaration.ProjectName);
                table.Add(declaration.FilePath);
                table.Add(declaration.Key);
            }

            foreach (var error in fragment.FileErrors)
            {
                table.Add(error.FilePath);
                table.Add(error.Error);
            }
        }

        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            table.Write(writer);
            writer.Write(table[SdkVersion]);
            writer.Write(Projects.Count);

            foreach (var fragment in Projects.Values)
            {
                writer.Write(table[fragment.ProjectName]);
                writer.Write(table[fragment.Fingerprint]);
                writer.Write(table[fragment.ContentHash]);
                writer.Write(table[fragment.SurfaceHash]);
                WriteNullable(writer, table, fragment.CompileError);

                writer.Write(fragment.Graph.NodeCount);
                foreach (var node in fragment.Graph.Nodes.Values)
                {
                    writer.Write(table[node.Key]);
                    writer.Write(table[node.DisplayName]);
                    writer.Write((byte)node.Kind);
                    writer.Write(table[node.ProjectName]);
                    WriteNullable(writer, table, node.ContainingTypeKey);
                    WriteNullable(writer, table, node.FilePath);
                }

                writer.Write(fragment.Graph.EdgeCount);
                foreach (var (from, to, kind) in fragment.Graph.Edges)
                {
                    writer.Write(table[from]);
                    writer.Write(table[to]);
                    writer.Write((int)kind);
                }

                writer.Write(fragment.Tests.Count);
                foreach (var test in fragment.Tests)
                {
                    writer.Write(table[test.SymbolKey]);
                    writer.Write(table[test.ClassKey]);
                    writer.Write(table[test.Namespace]);
                    writer.Write(table[test.ClassName]);
                    writer.Write(table[test.MethodName]);
                    writer.Write(table[test.ProjectName]);
                    writer.Write((byte)test.Framework);
                    writer.Write(test.IsParameterized);
                }

                writer.Write(fragment.Reflections.Count);
                foreach (var reflection in fragment.Reflections)
                {
                    writer.Write(table[reflection.Description]);
                    WriteNullable(writer, table, reflection.OwningMemberKey);
                    writer.Write(table[reflection.FilePath]);
                }

                writer.Write(fragment.Routes.Count);
                foreach (var route in fragment.Routes)
                {
                    writer.Write(table[route.Template]);
                    writer.Write(table[route.OwningMemberKey]);
                    writer.Write(route.IsEndpoint);
                    writer.Write(table[route.ProjectName]);
                    writer.Write(table[route.FilePath]);
                    writer.Write(route.Line);
                }

                writer.Write(fragment.TypeFacts.Count);
                foreach (var fact in fragment.TypeFacts)
                {
                    writer.Write(table[fact.MemberKey]);
                    writer.Write(fact.ObtainedTypeKeys.Count);
                    foreach (var typeKey in fact.ObtainedTypeKeys)
                    {
                        writer.Write(table[typeKey]);
                    }

                    writer.Write(fact.IsUnknown);
                }

                writer.Write(fragment.Declarations.Count);
                foreach (var declaration in fragment.Declarations)
                {
                    writer.Write(table[declaration.ProjectName]);
                    writer.Write(table[declaration.FilePath]);
                    writer.Write(table[declaration.Key]);
                    writer.Write(declaration.StartLine);
                    writer.Write(declaration.EndLine);
                    writer.Write(declaration.IsType);
                    writer.Write(declaration.IsInlined);
                }

                writer.Write(fragment.FileErrors.Count);
                foreach (var error in fragment.FileErrors)
                {
                    writer.Write(table[error.FilePath]);
                    writer.Write(table[error.Error]);
                }

                writer.Write(fragment.GeneratedDocumentCount);
            }
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static string? ReadNullable(BinaryReader reader, string[] strings)
    {
        var index = reader.ReadInt32();
        return index < 0 ? null : strings[index];
    }

    private static void WriteNullable(BinaryWriter writer, StringTable table, string? value) =>
        writer.Write(value is null ? -1 : table[value]);

    private static string[] ReadStringTable(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var strings = new string[count];
        for (var i = 0; i < count; i++)
        {
            strings[i] = reader.ReadString();
        }

        return strings;
    }

    private sealed class StringTable
    {
        private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
        private readonly List<string> _values = [];

        public int this[string value] => _indices[value];

        public void Add(string value)
        {
            if (_indices.TryAdd(value, _values.Count))
            {
                _values.Add(value);
            }
        }

        public void AddNullable(string? value)
        {
            if (value is not null)
            {
                Add(value);
            }
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(_values.Count);
            foreach (var value in _values)
            {
                writer.Write(value);
            }
        }
    }
}
