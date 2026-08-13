using Tia.Core.Analysis;
using Tia.Core.Caching;
using Tia.Core.Model;

namespace Tia.Workspace;

/// <summary>
/// Seeds every reflecting and serializing member, then traverses.
/// </summary>
/// <remarks>
/// <para>
/// Reflection means a member can reach things no static edge records, so the strongest sound
/// statement about it is that it is always impacted. Seeding the member says exactly that, and
/// then the graph does the rest: a reflecting test selects itself, while a reflecting library
/// method selects everything that reaches it - which is precisely the set at risk when, say, a
/// registry discovers a newly added type by scanning the assembly. Widening the whole project
/// instead selects the same members plus every unrelated test alongside them, and on a codebase
/// whose test suite uses reflection at all that collapses selection to nearly 100%.
/// </para>
/// <para>
/// "Always impacted" used to mean "if the traversal reaches it", which is not the same thing
/// and is wrong in the case that matters. The NodaTime gate found it: nothing statically
/// reaches <c>TestHelper.AssertXmlRoundtrip</c>, so it was never scanned, so the XmlSerializer
/// call inside it was never noticed - and breaking the pattern parser its serialized types use
/// failed sixty tests the selection had left out. A reflecting member is dangerous *because*
/// nothing reaches it. Every one in the solution is a seed now, found once per project when
/// its fragment is built and carried in the cache so a warm run pays nothing for it.
/// </para>
/// <para>
/// Only when something else changed, though. With an empty change set there is nothing for a
/// reflecting member to depend on, and a diff that touches nothing must still select nothing.
/// </para>
/// </remarks>
internal sealed class ReflectionSeeder
{
    public ImpactTraversal Seed(
        ImpactSelector selector,
        ImpactGraph graph,
        IReadOnlyList<(string Project, ReflectionRecord Record)> reflections,
        SymbolChangeSet changes,
        CancellationToken cancellationToken)
    {
        if (changes.IsEmpty)
        {
            return selector.Traverse(graph, changes.Keys, cancellationToken);
        }

        foreach (var group in reflections.GroupBy(r => (r.Project, r.Record.FilePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var records = group.Select(g => g.Record).ToList();
            var file = Path.GetFileName(group.Key.FilePath);

            foreach (var record in records)
            {
                if (record.OwningMemberKey is { } owner)
                {
                    changes.Keys.Add(owner);
                }
                else
                {
                    // Reflection outside any member - a top-level statement, a field initialiser
                    // the model could not resolve - leaves nothing to seed, so the project-wide
                    // reading is the only sound fallback.
                    changes.AddProjectWide(group.Key.Project, ProjectWideCause.Reflection,
                        $"{file} uses {record.Description} outside any member");
                }
            }

            changes.AddProjectWide(group.Key.Project, ProjectWideCause.Reflection,
                $"{file} uses {records[0].Description}" +
                (records.Count > 1 ? $" and {records.Count - 1} more" : string.Empty) +
                "; the reflecting member(s) are treated as always impacted",
                widensProject: false);
        }

        return selector.Traverse(graph, changes.Keys, cancellationToken);
    }
}
