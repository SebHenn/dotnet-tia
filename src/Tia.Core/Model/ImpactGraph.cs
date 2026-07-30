namespace Tia.Core.Model;

public enum SymbolNodeKind
{
    Type,
    Method,
    Property,
    Event,
    Field,
    Other,
}

[Flags]
public enum EdgeKind
{
    None = 0,

    /// <summary>Plain reference: an invocation, member access, object creation or identifier.</summary>
    Reference = 1,

    /// <summary>Interface member to an implementation of it. Followed downward.</summary>
    InterfaceToImplementation = 2,

    /// <summary>Virtual or abstract member to an override of it. Followed downward.</summary>
    VirtualToOverride = 4,

    /// <summary>An implementation to the interface member it implements. Followed upward.</summary>
    ImplementationToInterface = 256,

    /// <summary>An override to the member it overrides. Followed upward.</summary>
    OverrideToVirtual = 512,

    /// <summary>Base type or implemented interface to the deriving type.</summary>
    Derived = 8,

    /// <summary>Fixture plumbing (constructors, setup, class fixtures) to the tests it serves.</summary>
    Fixture = 16,

    /// <summary>A type to the members it declares. Followed when the type itself is impacted.</summary>
    Containment = 32,

    /// <summary>An attribute class to the symbol it annotates.</summary>
    Attribute = 64,
}

public sealed record SymbolNode
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public required SymbolNodeKind Kind { get; init; }

    public required string ProjectName { get; init; }

    public string? ContainingTypeKey { get; init; }

    /// <summary>A file that declares this symbol. Partial members have several; one is enough
    /// for the reflection widening check and for <c>explain</c>.</summary>
    public string? FilePath { get; init; }
}

/// <summary>
/// The reverse reference graph. Edges point from a symbol to the symbols that would have to be
/// re-examined if it changed, so impact analysis is a plain forward traversal of this graph.
/// </summary>
public sealed class ImpactGraph
{
    private readonly Dictionary<string, SymbolNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, EdgeKind>> _dependents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _typeMembers = new(StringComparer.Ordinal);

    public int NodeCount => _nodes.Count;

    public int TypeCount => _nodes.Values.Count(n => n.Kind == SymbolNodeKind.Type);

    public int EdgeCount
    {
        get
        {
            var total = 0;
            foreach (var entry in _dependents)
            {
                total += entry.Value.Count;
            }

            return total;
        }
    }

    public IReadOnlyDictionary<string, SymbolNode> Nodes => _nodes;

    public IEnumerable<(string From, string To, EdgeKind Kind)> Edges
    {
        get
        {
            foreach (var (from, dependents) in _dependents)
            {
                foreach (var (to, kind) in dependents)
                {
                    yield return (from, to, kind);
                }
            }
        }
    }

    public SymbolNode? TryGetNode(string key) => _nodes.GetValueOrDefault(key);

    public void AddNode(SymbolNode node)
    {
        if (_nodes.TryGetValue(node.Key, out var existing))
        {
            if (existing.FilePath is null && node.FilePath is not null)
            {
                _nodes[node.Key] = existing with { FilePath = node.FilePath };
            }
        }
        else
        {
            _nodes[node.Key] = node;
        }

        if (node.ContainingTypeKey is { } typeKey)
        {
            if (!_typeMembers.TryGetValue(typeKey, out var members))
            {
                members = new HashSet<string>(StringComparer.Ordinal);
                _typeMembers[typeKey] = members;
            }

            members.Add(node.Key);
        }
    }

    public void AddEdge(string referenced, string dependent, EdgeKind kind)
    {
        if (referenced.Length == 0 || dependent.Length == 0 || string.Equals(referenced, dependent, StringComparison.Ordinal))
        {
            return;
        }

        if (!_dependents.TryGetValue(referenced, out var map))
        {
            map = new Dictionary<string, EdgeKind>(StringComparer.Ordinal);
            _dependents[referenced] = map;
        }

        map[dependent] = map.TryGetValue(dependent, out var existing) ? existing | kind : kind;
    }

    public IReadOnlyDictionary<string, EdgeKind> DependentsOf(string key) =>
        _dependents.TryGetValue(key, out var map) ? map : EmptyEdges;

    public IReadOnlyCollection<string> MembersOfType(string typeKey) =>
        _typeMembers.TryGetValue(typeKey, out var members) ? members : [];

    /// <summary>Folds another graph (typically one project's fragment) into this one.</summary>
    public void Merge(ImpactGraph other)
    {
        foreach (var node in other._nodes.Values)
        {
            AddNode(node);
        }

        foreach (var (referenced, dependents) in other._dependents)
        {
            foreach (var (dependent, kind) in dependents)
            {
                AddEdge(referenced, dependent, kind);
            }
        }

        foreach (var (typeKey, members) in other._typeMembers)
        {
            if (!_typeMembers.TryGetValue(typeKey, out var existing))
            {
                _typeMembers[typeKey] = [.. members];
            }
            else
            {
                existing.UnionWith(members);
            }
        }
    }

    private static readonly Dictionary<string, EdgeKind> EmptyEdges = new(StringComparer.Ordinal);
}
