using Tia.Core.Model;

namespace Tia.DeadCode;

/// <summary>What a sweep found.</summary>
/// <param name="Live">Every key the roots can reach, hubs and other synthetic keys included.</param>
/// <param name="Unreached">
/// Declarations in source that no root reaches, ordered by file and line. A member of a type that
/// is itself unreached is not listed: the type is the finding, and its members follow from it.
/// </param>
/// <param name="Reason">
/// For every live key that a root did not name itself, the key that first reached it and how.
/// Following it back ends at a root.
/// </param>
public sealed record ReachabilityResult(
    IReadOnlySet<string> Live,
    IReadOnlyList<SymbolNode> Unreached,
    IReadOnlyDictionary<string, (string From, string Why)> Reason)
{
    /// <summary>The keys from a root down to <paramref name="key"/>, each with why it was reached; empty when nothing reaches it.</summary>
    public IReadOnlyList<(string Key, string? Why)> Chain(string key)
    {
        if (!Live.Contains(key))
        {
            return [];
        }

        var chain = new List<(string, string?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? current = key;
        while (current is not null && seen.Add(current))
        {
            // The reason belongs to the key it explains: how this one was reached from the one before it.
            if (Reason.TryGetValue(current, out var step))
            {
                chain.Add((current, step.Why));
                current = step.From;
            }
            else
            {
                chain.Add((current, null));
                current = null;
            }
        }

        chain.Reverse();
        return chain;
    }
}

/// <summary>
/// Marks everything the roots use, directly or through any chain of uses, and reports the rest.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ImpactGraph"/> stores <c>referenced -&gt; dependent</c>, because impact analysis asks
/// what a change reaches. This asks the opposite question - what does running code reach - so it
/// walks the same edges backwards: a symbol is live when something live depends on it.
/// </para>
/// <para>
/// Reachability rather than reference counting, which is the difference from tools that report
/// symbols with zero references: two methods that only call each other each have a reference, and
/// neither is reached from anything that runs.
/// </para>
/// <para>
/// Every edge kind is followed, in both directions of a declaration/implementation pair. That
/// over-approximates what is live - an implementation counts as used when its interface member is,
/// whether or not anything constructs its type - and over-approximating is the safe side here: a
/// symbol wrongly kept is a missed finding, a symbol wrongly reported is code someone deletes.
/// </para>
/// </remarks>
public static class Reachability
{
    /// <summary>
    /// Methods the runtime starts a program at: a <c>Main</c>, or the one the compiler synthesises
    /// for top-level statements.
    /// </summary>
    /// <remarks>
    /// By name, which is what the graph can answer today; it has no record of which method a
    /// compilation chose. A method merely called <c>Main</c> is therefore a root too - kept, not
    /// reported, so the imprecision cannot produce a wrong finding.
    /// </remarks>
    public static IEnumerable<string> EntryPoints(ImpactGraph graph) =>
        graph.Nodes.Values
            .Where(node => node.Kind == SymbolNodeKind.Method && SymbolKeys.SimpleNameOf(node.Key) is "Main" or "{Main}$")
            .Select(node => node.Key);

    public static ReachabilityResult Sweep(ImpactGraph graph, IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        var uses = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var calledFromOutside = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (referenced, dependent, kind) in graph.Edges)
        {
            // A member leading into one of its type's hubs is one that code outside the solution
            // calls on an instance: an override of a BCL virtual, an implementation of a BCL
            // interface, something a serializer reads. The impact engine asks where such an
            // instance is handed over; here it is enough that the type is live at all, because a
            // live `IDisposable` cannot have its `Dispose` deleted whether or not anything calls it.
            if (kind.HasFlag(EdgeKind.ExternalDispatch) &&
                graph.TryGetNode(referenced) is { Kind: not SymbolNodeKind.Other, ContainingTypeKey: { } owner } &&
                graph.TryGetNode(dependent) is { Kind: SymbolNodeKind.Other })
            {
                if (!calledFromOutside.TryGetValue(owner, out var members))
                {
                    members = [];
                    calledFromOutside[owner] = members;
                }

                members.Add(referenced);
            }

            if (!uses.TryGetValue(dependent, out var list))
            {
                list = [];
                uses[dependent] = list;
            }

            list.Add(referenced);
        }

        var live = new HashSet<string>(StringComparer.Ordinal);
        var reason = new Dictionary<string, (string From, string Why)>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var root in roots)
        {
            Mark(root, null, string.Empty);
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();

            if (uses.TryGetValue(current, out var used))
            {
                foreach (var key in used)
                {
                    Mark(key, current, "uses");
                }
            }

            if (graph.TryGetNode(current) is not { } node)
            {
                continue;
            }

            // A member cannot be live inside a type that is not.
            if (node.ContainingTypeKey is { } containingType)
            {
                Mark(containingType, current, "is a member of");
            }

            // A live type is one something names, and whatever names it may be what constructs it:
            // a container registration, a fixture declaration, a serializer, `typeof` handed to an
            // activator. None of those call a constructor the graph can see, so the constructors
            // go with the type. The static constructor runs on first touch for the same reason.
            if (node.Kind == SymbolNodeKind.Type)
            {
                foreach (var member in graph.MembersOfType(current))
                {
                    if (SymbolKeys.SimpleNameOf(member) is "#ctor" or "#cctor")
                    {
                        Mark(member, current, "has its constructor kept with");
                    }
                }

                if (calledFromOutside.TryGetValue(current, out var members))
                {
                    foreach (var member in members)
                    {
                        Mark(member, current, "is called from outside through");
                    }
                }
            }
        }

        // One file compiled into several assemblies - a source file linked into a test project, or a
        // project built once per target framework - is several declarations to the graph and one to
        // whoever deletes it. Dead in one copy and live in another is live: the copy that is used
        // is the same text. Matched by file and display name rather than by line, because a
        // `#if` above the declaration moves the line between copies and a missed match here would
        // report live code as dead. The key stands in for the display name, minus its project
        // prefix: an explicit interface implementation shares its sibling property's display name,
        // and matching on that would hide a dead one behind a live one.
        var liveSites = graph.Nodes.Values
            .Where(node => node.FilePath is { Length: > 0 } && live.Contains(node.Key))
            .Select(node => (node.FilePath!, SiteKey(node)))
            .ToHashSet();

        // `Other` is what the graph's hubs are - a type "as a serializer reads it", a project's
        // session-wide setup. They carry a file so `explain` can point somewhere, and they are
        // not declarations anyone could delete.
        var unreached = graph.Nodes.Values
            .Where(node => node.Kind != SymbolNodeKind.Other && node.FilePath is { Length: > 0 } && !live.Contains(node.Key))
            .Where(node => !liveSites.Contains((node.FilePath!, SiteKey(node))))
            .Where(node => node.ContainingTypeKey is not { } type || live.Contains(type))
            .OrderBy(node => node.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Line)
            .ThenBy(node => node.Key, StringComparer.Ordinal)
            .ToList();

        return new ReachabilityResult(live, unreached, reason);

        static string SiteKey(SymbolNode node) =>
            node.Key[(node.Key.IndexOf('|', StringComparison.Ordinal) + 1)..];

        void Mark(string key, string? from, string why)
        {
            if (live.Add(key))
            {
                if (from is not null)
                {
                    reason[key] = (from, why);
                }

                queue.Enqueue(key);
            }
        }
    }
}
