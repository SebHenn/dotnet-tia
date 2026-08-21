using Tia.Core.Model;

namespace Tia.Core.Analysis;

public sealed record ImpactTraversal
{
    public required IReadOnlySet<string> Impacted { get; init; }

    public required IReadOnlySet<string> Seeds { get; init; }

    /// <summary>For every reached node, the node it was reached from and the edge that led there.
    /// This is what <c>explain</c> replays.</summary>
    public required IReadOnlyDictionary<string, (string From, EdgeKind Kind)> Predecessors { get; init; }

    /// <summary>
    /// How many hops the traversal took to reach a node, or <see cref="int.MaxValue"/> when it
    /// never did.
    /// </summary>
    /// <remarks>
    /// Deliberately the length of the path <c>explain</c> would print, rather than a separate
    /// notion of distance: a rank a reader cannot see the derivation of is a number to be taken on
    /// faith, and this one has a printable proof. The first walk is breadth-first, so for anything
    /// it reaches this is the true shortest hop count; a node added later by generalising an
    /// implementation to the declaration it satisfies carries the path it was actually reached by,
    /// which is longer and should be.
    /// </remarks>
    public int HopsTo(string key)
    {
        var path = PathTo(key);
        return path.Count == 0 ? int.MaxValue : path.Count;
    }

    /// <summary>Walks back from a node to the seed it was reached from, seed first.</summary>
    public IReadOnlyList<(string Key, EdgeKind IncomingEdge)> PathTo(string key)
    {
        if (!Impacted.Contains(key))
        {
            return [];
        }

        var path = new List<(string, EdgeKind)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = key;

        // Each node carries the edge that led *into* it, which is what the field is called and what
        // both renderers assume. Walking backwards, that is the predecessor step of the node being
        // added - not the one carried over from the node after it. Attaching it to the node the
        // edge led *out of* put every label one place too early, so a two-node path showed the
        // seed's label as None and printed "referenced by" whatever the edge actually was.
        while (seen.Add(current))
        {
            if (!Predecessors.TryGetValue(current, out var step))
            {
                path.Add((current, EdgeKind.None));
                break;
            }

            path.Add((current, step.Kind));
            current = step.From;
        }

        path.Reverse();
        return path;
    }
}

/// <summary>
/// Traversal of the reverse reference graph from the changed symbols.
/// </summary>
/// <remarks>
/// <para>
/// Reaching a type node fans out to the members it declares: a type is only ever reached because
/// its declaration - base list, attributes, type parameters - depends on something that changed,
/// and that affects everything inside it.
/// </para>
/// <para>
/// The hard part is polymorphism. A change to an implementation has to reach code that only knows
/// the interface, because at run time that code may dispatch to the changed type. Following the
/// edge upward and then onward without qualification is what makes selection collapse on any
/// library with a shared engine behind an interface: every consumer of the interface is reached,
/// which on FluentValidation is every test there is.
/// </para>
/// <para>
/// But the consequence can be bounded. Code that reaches <c>IPropertyValidator.IsValid</c> is only
/// affected by a change to <c>EnumValidator.IsValid</c> if it can also reach an <c>EnumValidator</c>
/// to dispatch to. So what an upward hop reaches is intersected with what can reach the
/// implementing type - through its constructor, a factory, a DI registration, a type argument, or
/// any other static mention. Instantiation that is not statically visible is reflection, and
/// reflection already makes the reflecting member unconditionally impacted.
/// </para>
/// <para>
/// With a <see cref="TypeFlowIndex"/> that bound narrows from "can reach the type" to "can obtain
/// an instance of it", which drops the mentions that cannot dispatch - a <c>typeof</c>, a static
/// call, a name in a base list. Only the bound changes: the walk stays unqualified, because
/// qualifying it is what the reverted attempt did and it turned precision into a miss.
/// </para>
/// </remarks>
public sealed class ImpactSelector(TypeFlowIndex? typeFlow = null)
{
    private const EdgeKind Upward = EdgeKind.ImplementationToInterface | EdgeKind.OverrideToVirtual;

    private const EdgeKind Downward = EdgeKind.InterfaceToImplementation | EdgeKind.VirtualToOverride;

    private readonly Dictionary<string, bool> _containerResolved = new(StringComparer.Ordinal);

    private readonly Dictionary<string, IReadOnlySet<string>> _narrowedByType = new(StringComparer.Ordinal);

    public ImpactTraversal Traverse(ImpactGraph graph, IEnumerable<string> seeds, CancellationToken cancellationToken = default)
    {
        var seedSet = new HashSet<string>(seeds, StringComparer.Ordinal);
        var predecessors = new Dictionary<string, (string From, EdgeKind Kind)>(StringComparer.Ordinal);

        // Everything reachable without ever generalising an implementation to the declaration it
        // satisfies.
        var impacted = Walk(graph, seedSet, allowUpward: false, predecessors, cancellationToken);

        var generalised = new HashSet<string>(StringComparer.Ordinal);
        var reachableFromType = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var reachableFromDeclaration = new Dictionary<string, (IReadOnlySet<string> Nodes, IReadOnlyDictionary<string, (string From, EdgeKind Kind)> Paths)>(StringComparer.Ordinal);

        // Each round can only add nodes, and a node added here may itself implement something.
        for (var round = 0; round < 8; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var added = false;

            foreach (var implementation in impacted.ToList())
            {
                foreach (var (declaration, kind) in graph.DependentsOf(implementation))
                {
                    if (!IsOnly(kind, Upward) || !generalised.Add(implementation + "" + declaration))
                    {
                        continue;
                    }

                    added |= Generalise(
                        graph, implementation, declaration, kind, impacted, predecessors,
                        reachableFromType, reachableFromDeclaration, cancellationToken);
                }
            }

            if (!added)
            {
                break;
            }
        }

        return new ImpactTraversal
        {
            Impacted = impacted,
            Seeds = seedSet,
            Predecessors = predecessors,
        };
    }

    /// <summary>
    /// Follows one implementation-to-declaration edge and adds what it can legitimately reach.
    /// </summary>
    private bool Generalise(
        ImpactGraph graph,
        string implementation,
        string declaration,
        EdgeKind kind,
        HashSet<string> impacted,
        Dictionary<string, (string From, EdgeKind Kind)> predecessors,
        Dictionary<string, IReadOnlySet<string>> reachableFromType,
        Dictionary<string, (IReadOnlySet<string> Nodes, IReadOnlyDictionary<string, (string From, EdgeKind Kind)> Paths)> reachableFromDeclaration,
        CancellationToken cancellationToken)
    {
        if (!reachableFromDeclaration.TryGetValue(declaration, out var fromDeclaration))
        {
            var paths = new Dictionary<string, (string From, EdgeKind Kind)>(StringComparer.Ordinal);
            // Upward edges stay enabled inside this walk. Disabling them - so that every
            // generalisation earns its own bound - is more precise in principle and was tried;
            // on FluentValidation it selected nothing at all, because the chain of hops through a
            // layered validator hierarchy died before reaching any test. Precision that turns
            // into a miss is not precision, so the walk stays unqualified past the first hop and
            // the bound below is what does the work.
            //
            // Restricted seed: the declaration was itself reached by an upward hop, so descending
            // from it to sibling implementations is the false inference.
            var nodes = Walk(graph, [declaration], allowUpward: true, paths, cancellationToken, seedsRestricted: true);
            fromDeclaration = (nodes, paths);
            reachableFromDeclaration[declaration] = fromDeclaration;
        }

        var typeKey = graph.TryGetNode(implementation)?.ContainingTypeKey;

        IReadOnlySet<string> permitted;
        if (typeKey is null)
        {
            // Nothing to bound it with, so the unqualified reading is the only sound one.
            permitted = fromDeclaration.Nodes;
        }
        else
        {
            if (!reachableFromType.TryGetValue(typeKey, out var fromType))
            {
                // Without upward edges: generalising *this* walk would defeat its purpose, since
                // the question is precisely what can get hold of the concrete type.
                fromType = Walk(graph, [typeKey], allowUpward: false, predecessors: null, cancellationToken);
                reachableFromType[typeKey] = fromType;
            }

            // Nothing outside the type itself mentions it anywhere in the solution, so nothing
            // observable creates it - a container registering by convention, a plugin loaded from
            // another assembly, a deserialiser. Whoever does, we cannot see them, and a bound
            // drawn from an empty set would exclude every caller of the interface. This is the
            // case that makes dependency injection work, and it has to fall back.
            permitted = ReachesBeyondItself(graph, typeKey, fromType)
                ? NarrowToObtainers(typeKey, fromType)
                : fromDeclaration.Nodes;
        }

        var added = false;

        foreach (var node in fromDeclaration.Nodes)
        {
            if (typeKey is not null && !permitted.Contains(node) && node != declaration)
            {
                continue;
            }

            if (impacted.Add(node))
            {
                added = true;
            }

            if (!predecessors.ContainsKey(node))
            {
                predecessors[node] = node == declaration
                    ? (implementation, kind)
                    : fromDeclaration.Paths.GetValueOrDefault(node, (declaration, EdgeKind.Reference));
            }
        }

        predecessors.TryAdd(declaration, (implementation, kind));
        return added;
    }

    /// <summary>
    /// Keeps only the nodes that could hold an instance of the type, out of those that can reach it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Intersecting rather than replacing is deliberate: the result is always a subset of today's
    /// bound, so turning the flag on can only ever narrow a selection. That makes the measurement
    /// mean one thing - whatever changes is this - and confines the risk to the one class worth
    /// watching, a test dropped that should have run.
    /// </para>
    /// <para>
    /// The two escapes are the same one the unbounded case takes. If nothing anywhere is seen to
    /// obtain the type, its construction is invisible and an empty bound would exclude everyone. If
    /// the intersection is empty although obtainers exist, the flow analysis and the reachability
    /// walk disagree about a type that demonstrably reaches somewhere - which is a gap in the flow
    /// rules rather than proof that nothing dispatches - and the wider answer is the one to keep.
    /// </para>
    /// </remarks>
    private IReadOnlySet<string> NarrowToObtainers(string typeKey, IReadOnlySet<string> fromType)
    {
        if (typeFlow is null || !typeFlow.CanBound(typeKey))
        {
            return fromType;
        }

        if (_narrowedByType.TryGetValue(typeKey, out var cached))
        {
            return cached;
        }

        var narrowed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in fromType)
        {
            if (typeFlow.Permits(node, typeKey))
            {
                narrowed.Add(node);
            }
        }

        var result = narrowed.Count > 0 ? narrowed : fromType;
        if (result.Count < fromType.Count)
        {
            NarrowedHops++;
        }

        _narrowedByType[typeKey] = result;
        return result;
    }

    /// <summary>How many upward hops the flow bound actually narrowed, for the report. A run that
    /// reports zero has spent the scan and told the selection nothing.</summary>
    public int NarrowedHops { get; private set; }

    /// <summary>True when something other than the type's own declarations can reach it.</summary>
    private static bool ReachesBeyondItself(ImpactGraph graph, string typeKey, IReadOnlySet<string> reached)
    {
        var members = graph.MembersOfType(typeKey);

        foreach (var node in reached)
        {
            if (node != typeKey && !members.Contains(node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether nothing in the solution names the type this node belongs to, so a container
    /// resolving it is the only explanation for how it ever runs.
    /// </summary>
    /// <remarks>
    /// This is the guard that makes the request-type edge affordable. A handler discovered by
    /// assembly scanning has no static mention anywhere, and following its request type is the only
    /// way to reach the code that dispatches to it. A type that *is* mentioned - constructed by a
    /// test, registered by name, passed as a type argument - already has ordinary edges, and
    /// following the request type as well would connect every <c>AbstractValidator&lt;Person&gt;</c>
    /// to everything that touches a <c>Person</c>. Same reasoning as the bound on the interface
    /// hop, and the same conclusion: the interesting case is the one nothing can see.
    /// </remarks>
    private bool IsResolvedOnlyByContainer(ImpactGraph graph, string nodeKey)
    {
        var typeKey = graph.TryGetNode(nodeKey) is { } node
            ? node.Kind == SymbolNodeKind.Type ? nodeKey : node.ContainingTypeKey
            : null;

        if (typeKey is null)
        {
            return false;
        }

        if (_containerResolved.TryGetValue(typeKey, out var cached))
        {
            return cached;
        }

        var members = graph.MembersOfType(typeKey);
        var mentioned = false;

        foreach (var (dependent, kind) in graph.DependentsOf(typeKey))
        {
            // Its own members and the request edge just added do not count as being named.
            if (kind is EdgeKind.DispatchByRequest or EdgeKind.DispatchByRoute ||
                dependent == typeKey || members.Contains(dependent))
            {
                continue;
            }

            // Nor does a node that is itself inside the type.
            if (graph.TryGetNode(dependent)?.ContainingTypeKey == typeKey)
            {
                continue;
            }

            mentioned = true;
            break;
        }

        _containerResolved[typeKey] = !mentioned;
        return !mentioned;
    }

    /// <summary>
    /// Breadth-first walk. When upward edges are allowed, a node reached by one is marked
    /// restricted and may not be left by a downward edge - otherwise a change to one
    /// implementation would reach its siblings through the declaration they share.
    /// </summary>
    private HashSet<string> Walk(
        ImpactGraph graph,
        IEnumerable<string> seeds,
        bool allowUpward,
        Dictionary<string, (string From, EdgeKind Kind)>? predecessors,
        CancellationToken cancellationToken,
        bool seedsRestricted = false)
    {
        var seedSet = new HashSet<string>(seeds, StringComparer.Ordinal);
        var free = seedsRestricted ? new HashSet<string>(StringComparer.Ordinal) : seedSet;
        var restricted = seedsRestricted ? seedSet : new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Key, bool Restricted)>(seedSet.Select(s => (s, seedsRestricted)));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, isRestricted) = queue.Dequeue();

            foreach (var (dependent, kind) in graph.DependentsOf(current))
            {
                var isUpward = IsOnly(kind, Upward);

                if (isUpward && !allowUpward)
                {
                    continue;
                }

                if (isRestricted && IsOnly(kind, Downward))
                {
                    continue;
                }

                // Both dispatch edges are guarded the same way and for the same reason: they exist
                // to reach code that nothing names, and following them from something that *is*
                // named would connect every endpoint to everything that mentions a similar string.
                if (IsOnly(kind, EdgeKind.DispatchByRequest | EdgeKind.DispatchByRoute) &&
                    !IsResolvedOnlyByContainer(graph, current))
                {
                    continue;
                }

                Enqueue(dependent, isUpward, current, kind);
            }

            if (graph.TryGetNode(current)?.Kind != SymbolNodeKind.Type)
            {
                continue;
            }

            foreach (var member in graph.MembersOfType(current))
            {
                Enqueue(member, nextRestricted: false, current, EdgeKind.Containment);
            }
        }

        free.UnionWith(restricted);
        return free;

        void Enqueue(string key, bool nextRestricted, string from, EdgeKind kind)
        {
            if (free.Contains(key))
            {
                return;
            }

            if (nextRestricted)
            {
                if (!restricted.Add(key))
                {
                    return;
                }
            }
            else
            {
                // Arriving unrestricted at a node previously reached restricted opens up edges
                // that were skipped, so it has to be walked again.
                restricted.Remove(key);
                free.Add(key);
            }

            predecessors?.TryAdd(key, (from, kind));
            queue.Enqueue((key, nextRestricted));
        }
    }

    private static bool IsOnly(EdgeKind kind, EdgeKind mask) => (kind & mask) != 0 && (kind & ~mask) == 0;
}
