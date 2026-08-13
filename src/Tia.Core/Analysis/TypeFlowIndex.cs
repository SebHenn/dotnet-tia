using Tia.Core.Caching;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// The per-project type facts, joined across the whole solution and propagated to a fixpoint.
/// </summary>
/// <remarks>
/// <para>
/// One question, asked once per upward hop: can this node get hold of an instance of that concrete
/// type? A member obtains what it names directly (<see cref="TypeFlow"/>) plus everything obtained
/// by whatever it references, because a call can hand its caller anything it made. That transitive
/// half is the fixpoint, and it is cross-project - a test in one assembly reaching a factory in
/// another is the ordinary case - so it runs after the fragments have been merged, alongside
/// <c>RouteSeeder</c> and for the same reason.
/// </para>
/// <para>
/// <b>Unknown permits.</b> Reflection, <c>dynamic</c>, an instance arriving from outside the graph,
/// or simply too many types to be worth tracking: each of those makes the answer "any of them", and
/// the hop is allowed. That is the direction the reverted attempt got backwards. A bound that is
/// too generous costs selection ratio, which is measurable and recoverable; a bound that is too
/// tight costs a test that would have failed, which is the one defect this project treats as fatal.
/// </para>
/// </remarks>
public sealed class TypeFlowIndex
{
    /// <summary>
    /// Where a node stops tracking individual types and answers "any of them".
    /// </summary>
    /// <remarks>
    /// Both a cost bound and a soundness-preserving one. A member near the root of a large
    /// dependency tree accumulates most of the solution, at which point the set costs memory to
    /// carry, time to union, and excludes nothing worth excluding. Saturating to unknown is the
    /// same answer the analysis would have reached slowly, and it keeps the fixpoint linear in
    /// edges rather than in edges times types.
    /// </remarks>
    private const int SaturationLimit = 96;

    private readonly Dictionary<string, int> _typeIds;
    private readonly Dictionary<string, HashSet<int>> _obtainable;
    private readonly HashSet<string> _unknown;
    private readonly HashSet<string> _hasObtainers;

    private TypeFlowIndex(
        Dictionary<string, int> typeIds,
        Dictionary<string, HashSet<int>> obtainable,
        HashSet<string> unknown,
        HashSet<string> hasObtainers)
    {
        _typeIds = typeIds;
        _obtainable = obtainable;
        _unknown = unknown;
        _hasObtainers = hasObtainers;
    }

    /// <summary>How many types ended up with a usable flow bound. Reported, so a run that computed
    /// nothing says so instead of looking like a run that found nothing to narrow.</summary>
    public int BoundedTypes => _hasObtainers.Count;

    public int SaturatedNodes { get; private init; }

    /// <summary>
    /// Whether anything at all can be seen to obtain this type.
    /// </summary>
    /// <remarks>
    /// False is the invisible-construction case - a container registering by convention, a plugin
    /// from another assembly, a deserialiser - and it has to fall back to the unbounded reading for
    /// exactly the reason <c>ImpactSelector.ReachesBeyondItself</c> already does: a bound drawn from
    /// an empty set excludes every caller.
    /// </remarks>
    public bool CanBound(string typeKey) => _hasObtainers.Contains(typeKey);

    /// <summary>Whether <paramref name="nodeKey"/> may hold an instance of <paramref name="typeKey"/>.</summary>
    public bool Permits(string nodeKey, string typeKey)
    {
        if (_unknown.Contains(nodeKey))
        {
            return true;
        }

        return _typeIds.TryGetValue(typeKey, out var id) &&
               _obtainable.TryGetValue(nodeKey, out var set) &&
               set.Contains(id);
    }

    /// <summary>
    /// Joins the fragments' facts and propagates them over the merged graph.
    /// </summary>
    /// <param name="unknownMembers">
    /// Members already known to defeat static analysis - the reflecting and serializing sites
    /// <c>ReflectionScanner</c> found. They are seeded unknown rather than re-derived, because the
    /// scan that found them has already run and its verdict is the same one this needs.
    /// </param>
    public static TypeFlowIndex Resolve(
        ImpactGraph graph,
        IEnumerable<TypeFlowFact> facts,
        IEnumerable<string> unknownMembers,
        CancellationToken cancellationToken = default)
    {
        // Only types that are the containing type of an implementation ever have a bound drawn on
        // them, so they are the only ones worth an id. On a solution with one interface and a
        // thousand plain classes that is the difference between a set per member and nothing at all.
        var typeIds = BoundedTypeIds(graph, cancellationToken);
        var hubs = ConstructionHubs(graph, cancellationToken);
        var state = new Fixpoint(typeIds);

        foreach (var member in unknownMembers)
        {
            state.MarkUnknown(member);
        }

        foreach (var fact in facts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Seed(fact);
        }

        state.Run(graph, hubs, cancellationToken);
        return state.ToIndex();
    }

    /// <summary>
    /// The worklist itself, propagating only what each node newly learned.
    /// </summary>
    /// <remarks>
    /// Pushing a node's whole set down every out-edge each time it grew was the obvious shape and
    /// is quadratic in the set size: on a solution with a few hundred thousand edges it is billions
    /// of set operations. Each type id crosses each edge at most once here, so the work is bounded
    /// by edges times <see cref="SaturationLimit"/> however tangled the graph is.
    /// </remarks>
    private sealed class Fixpoint(Dictionary<string, int> typeIds)
    {
        private readonly Dictionary<string, HashSet<int>> _obtainable = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<int>> _pending = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unknown = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingUnknown = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hasObtainers = new(StringComparer.Ordinal);
        private readonly Queue<string> _queue = new();
        private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
        private int _saturated;

        public void Seed(TypeFlowFact fact)
        {
            if (fact.IsUnknown)
            {
                MarkUnknown(fact.MemberKey);
            }

            foreach (var typeKey in fact.ObtainedTypeKeys)
            {
                if (typeIds.TryGetValue(typeKey, out var id))
                {
                    _hasObtainers.Add(typeKey);
                    Learn(fact.MemberKey, id);
                }
            }
        }

        public void Run(ImpactGraph graph, IReadOnlyDictionary<string, List<string>> hubs, CancellationToken cancellationToken)
        {
            while (_queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = _queue.Dequeue();
                _queued.Remove(current);

                var spreadsUnknown = _pendingUnknown.Remove(current);
                _pending.Remove(current, out var delta);

                foreach (var successor in Successors(graph, hubs, current))
                {
                    if (spreadsUnknown)
                    {
                        MarkUnknown(successor);
                    }
                    else if (delta is not null)
                    {
                        foreach (var id in delta)
                        {
                            Learn(successor, id);
                        }
                    }
                }
            }
        }

        public TypeFlowIndex ToIndex() =>
            new(typeIds, _obtainable, _unknown, _hasObtainers) { SaturatedNodes = _saturated };

        /// <summary>
        /// Once a node answers "any type", the individual ids it was carrying stop mattering -
        /// unknown already permits everything they would have. Dropping them is what keeps a
        /// saturated hub from paying the cost it saturated to avoid.
        /// </summary>
        public void MarkUnknown(string key)
        {
            if (!_unknown.Add(key))
            {
                return;
            }

            _obtainable.Remove(key);
            _pending.Remove(key);
            _pendingUnknown.Add(key);
            Enqueue(key);
        }

        private void Learn(string key, int id)
        {
            if (_unknown.Contains(key))
            {
                return;
            }

            if (!_obtainable.TryGetValue(key, out var set))
            {
                set = [];
                _obtainable[key] = set;
            }

            if (!set.Add(id))
            {
                return;
            }

            if (set.Count > SaturationLimit)
            {
                _saturated++;
                MarkUnknown(key);
                return;
            }

            if (!_pending.TryGetValue(key, out var delta))
            {
                delta = [];
                _pending[key] = delta;
            }

            delta.Add(id);
            Enqueue(key);
        }

        private void Enqueue(string key)
        {
            if (_queued.Add(key))
            {
                _queue.Enqueue(key);
            }
        }
    }

    /// <summary>
    /// The nodes that can receive what <paramref name="key"/> obtained.
    /// </summary>
    /// <remarks>
    /// The reverse-reference edges point from a symbol to whatever depends on it, which is exactly
    /// the direction a value travels: if a member calls a factory, it can be handed what the factory
    /// made. Two kinds are left out. The downward edges - interface member to implementation,
    /// virtual to override - run the wrong way for a value; propagating along them would claim an
    /// implementation holds whatever the interface's callers hold. The two dispatch edges are not
    /// value flow at all, only a guarded guess that two members meet at run time.
    /// </remarks>
    private static IEnumerable<string> Successors(
        ImpactGraph graph,
        IReadOnlyDictionary<string, List<string>> hubs,
        string key)
    {
        const EdgeKind Excluded =
            EdgeKind.InterfaceToImplementation | EdgeKind.VirtualToOverride |
            EdgeKind.DispatchByRequest | EdgeKind.DispatchByRoute;

        foreach (var (dependent, kind) in graph.DependentsOf(key))
        {
            if ((kind & ~Excluded) != 0)
            {
                yield return dependent;
            }
        }

        if (hubs.TryGetValue(key, out var through))
        {
            foreach (var node in through)
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// Extra edges routing a type's construction-time state to every member of the type, and on to
    /// every type derived from it.
    /// </summary>
    /// <remarks>
    /// A base class whose constructor builds a <c>German</c> and stores it in a field, and a derived
    /// test that reads the field, are connected by nothing the reference graph records - the
    /// constructor call is implicit and the field read names the field, not the constructor. Since
    /// that is how a fixture is usually written, missing it would not be an edge case. Routing
    /// constructors and fields through the type node lets the existing <c>Derived</c> edge carry it
    /// to the subclass, which is the same path inheritance itself takes.
    /// </remarks>
    private static Dictionary<string, List<string>> ConstructionHubs(ImpactGraph graph, CancellationToken cancellationToken)
    {
        var hubs = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.Kind != SymbolNodeKind.Type)
            {
                continue;
            }

            foreach (var member in graph.MembersOfType(node.Key))
            {
                // The type's own members receive whatever its construction produced.
                Link(node.Key, member);

                if (graph.TryGetNode(member) is { } declared && IsConstructionState(declared))
                {
                    Link(member, node.Key);
                }
            }
        }

        return hubs;

        void Link(string from, string to)
        {
            if (!hubs.TryGetValue(from, out var targets))
            {
                targets = [];
                hubs[from] = targets;
            }

            targets.Add(to);
        }
    }

    /// <summary>Members whose value is fixed when an instance is built, so every other member of
    /// the type - and of any type derived from it - can read what they hold.</summary>
    private static bool IsConstructionState(SymbolNode node) =>
        node.Kind is SymbolNodeKind.Field or SymbolNodeKind.Property ||
        (node.Kind == SymbolNodeKind.Method && node.Key.Contains("#ctor", StringComparison.Ordinal));

    /// <summary>Types a bound could ever be drawn on: the ones declaring a member that generalises
    /// upward to an interface or a virtual.</summary>
    private static Dictionary<string, int> BoundedTypeIds(ImpactGraph graph, CancellationToken cancellationToken)
    {
        const EdgeKind Upward = EdgeKind.ImplementationToInterface | EdgeKind.OverrideToVirtual;
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (from, _, kind) in graph.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ((kind & Upward) == 0 || (kind & ~Upward) != 0)
            {
                continue;
            }

            if (graph.TryGetNode(from)?.ContainingTypeKey is { } typeKey)
            {
                ids.TryAdd(typeKey, ids.Count);
            }
        }

        return ids;
    }
}
