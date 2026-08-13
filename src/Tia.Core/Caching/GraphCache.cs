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
    private const int FormatVersion = 6;

    public required string SdkVersion { get; init; }

    public required Dictionary<string, ProjectGraphFragment> Projects { get; init; }

    public static GraphCache Empty(string sdkVersion) => new()
    {
        SdkVersion = sdkVersion,
        Projects = new Dictionary<string, ProjectGraphFragment>(StringComparer.Ordinal),
    };

    /// <summary>Cache file name for a solution. The key isolates unrelated solutions and SDKs.</summary>
    public static string FileName(string solutionPath, string sdkVersion) =>
        $"graph-{ShortHash(solutionPath + "|" + sdkVersion)}.bin";

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

                projects[name] = new ProjectGraphFragment
                {
                    ProjectName = name,
                    Fingerprint = fingerprint,
                    ContentHash = contentHash,
                    SurfaceHash = surfaceHash,
                    CompileError = compileError,
                    Reflections = reflections,
                    Routes = routes,
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
