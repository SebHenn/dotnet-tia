using System.Security.Cryptography;
using System.Text;
using Tia.Core.Model;

namespace Tia.Core.Caching;

/// <summary>One project's slice of the graph, keyed by a fingerprint of everything that produced it.</summary>
public sealed record ProjectGraphFragment
{
    public required string ProjectName { get; init; }

    public required string Fingerprint { get; init; }

    public required ImpactGraph Graph { get; init; }

    public required IReadOnlyList<TestMethod> Tests { get; init; }
}

/// <summary>
/// The on-disk graph cache.
/// </summary>
/// <remarks>
/// Invalidation is per project, and a project is invalidated when it *or any project it depends
/// on* changes: its edges point at symbol keys owned by its dependencies, so a change over there
/// can leave them stale. Cold graph construction is the main performance risk on a large solution
/// and this is what keeps a warm run cheap.
/// </remarks>
public sealed class GraphCache
{
    private const uint Magic = 0x47414954; // "TIAG"
    private const int FormatVersion = 1;

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

                projects[name] = new ProjectGraphFragment
                {
                    ProjectName = name,
                    Fingerprint = fingerprint,
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
