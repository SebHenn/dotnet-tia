using Tia.Core.Analysis;
using Tia.Core.Caching;
using Tia.Core.Model;

namespace Tia.Workspace;

/// <summary>
/// Joins route templates to the members that name them, after the fragments have been merged.
/// </summary>
/// <remarks>
/// <para>
/// A functional test reaches its endpoint by URL. It names a route string and a response shape,
/// never the endpoint class, and the framework maps one to the other by registration - so nothing
/// in the symbol graph connects them and a change to the endpoint selects nothing.
/// </para>
/// <para>
/// This runs after the merge for the same reason <see cref="ReflectionSeeder"/> does: the two ends
/// of the join are routinely in different projects, and a fragment only ever sees its own. Each
/// project's mentions travel in its fragment, and the edge is added globally once they are all
/// present.
/// </para>
/// <para>
/// Adding edges can only ever widen a selection, so this cannot introduce a miss by construction.
/// What stops it widening *everything* is the guard in <c>ImpactSelector</c>: the edge is followed
/// only when nothing in the solution names the endpoint's type. An endpoint registered by naming
/// its handler already has ordinary edges and does not need this one.
/// </para>
/// </remarks>
internal static class RouteSeeder
{
    /// <summary>Adds one edge per (endpoint, mentioning member) pair whose routes match.</summary>
    /// <returns>How many edges were added, for the report.</returns>
    public static int Seed(
        ImpactGraph graph,
        IReadOnlyList<RouteRecord> routes,
        CancellationToken cancellationToken = default)
    {
        var endpoints = new List<RouteRecord>();
        var references = new List<RouteRecord>();

        foreach (var record in routes)
        {
            (record.IsEndpoint ? endpoints : references).Add(record);
        }

        if (endpoints.Count == 0 || references.Count == 0)
        {
            return 0;
        }

        var added = 0;

        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var reference in references)
            {
                // A member that names the route it serves is not dispatching to itself.
                if (string.Equals(endpoint.OwningMemberKey, reference.OwningMemberKey, StringComparison.Ordinal) ||
                    !RouteTemplateIndex.Matches(endpoint.Template, reference.Template))
                {
                    continue;
                }

                graph.AddEdge(endpoint.OwningMemberKey, reference.OwningMemberKey, EdgeKind.DispatchByRoute);
                added++;
            }
        }

        return added;
    }
}
