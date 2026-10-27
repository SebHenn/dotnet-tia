using Tia.Core.Analysis;
using Tia.Core.Model;
using Tia.Core.Reporting;

namespace Tia.Workspace;

/// <summary>
/// Which test projects in a language this engine does not analyse have to run, whole (#44).
/// </summary>
/// <remarks>
/// <para>
/// Nothing inside an F# or VB test project can be selected, because it contributes no symbols. It
/// used to contribute nothing else either: <c>run</c> invoked C# test projects only, so such a
/// project ran on no diff at all, a full run included, and a change it would have caught passed.
/// </para>
/// <para>
/// So it runs unfiltered whenever it could be affected: on a full run, and on a selective one when
/// anything in its transitive references holds an impacted member or was widened - including the
/// project itself, which a changed <c>.fs</c> or <c>.vb</c> file widens. The references are
/// transitive because the compiler lets it use what its references reference. That is coarse, and
/// it is the finest answer available without its symbols.
/// </para>
/// </remarks>
public static class UnanalysedTests
{
    public static IReadOnlyList<UnanalysedTestProject> ForFullRun(IReadOnlyList<ForeignProject> foreign) =>
    [
        .. foreign.Where(f => f.IsTestProject).Select(f => Entry(f, runs: true, "full run")),
    ];

    /// <param name="affectedProjects">
    /// Every project that holds an impacted member or was widened, by name.
    /// </param>
    public static IReadOnlyList<UnanalysedTestProject> ForSelectiveRun(
        IReadOnlyList<ForeignProject> foreign,
        IReadOnlyList<ProjectDescriptor> descriptors,
        IReadOnlySet<string> affectedProjects)
    {
        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            references.TryAdd(descriptor.Name, descriptor.ProjectReferences);
        }

        foreach (var project in foreign)
        {
            references.TryAdd(project.Name, project.ProjectReferences);
        }

        var entries = new List<UnanalysedTestProject>();
        foreach (var project in foreign.Where(f => f.IsTestProject))
        {
            var cause = FirstAffected(project.Name, references, affectedProjects);
            entries.Add(cause is null
                ? Entry(project, runs: false, "nothing it references was impacted")
                : Entry(project, runs: true, cause == project.Name
                    ? "a file in it changed"
                    : $"it references {cause}, which was impacted"));
        }

        return entries;
    }

    /// <summary>
    /// The entries <c>run</c> leaves out, as <see cref="AnalysisReport.SkippedProjects"/> lists them
    /// (#130). Taken before any shard is cut, so a project another shard runs is never among them.
    /// </summary>
    public static IReadOnlyList<SkippedProject> Skipped(IReadOnlyList<UnanalysedTestProject> entries) =>
    [
        .. entries.Where(e => !e.Runs).Select(e => new SkippedProject { Project = e.Name, Assembly = e.Assembly, Reason = e.Reason }),
    ];

    /// <summary>The affected projects among a graph's impacted members, the widened projects and the project-wide changes.</summary>
    public static IReadOnlySet<string> AffectedProjects(
        ImpactGraph graph,
        ImpactTraversal traversal,
        IReadOnlySet<string> widenedProjects,
        IEnumerable<string> projectWideChanges)
    {
        var affected = new HashSet<string>(widenedProjects, StringComparer.Ordinal);
        affected.UnionWith(projectWideChanges);

        foreach (var key in traversal.Impacted)
        {
            if (graph.Nodes.TryGetValue(key, out var node))
            {
                affected.Add(node.ProjectName);
            }
        }

        return affected;
    }

    /// <summary>The first affected project reachable from <paramref name="start"/> through references, itself included.</summary>
    private static string? FirstAffected(
        string start,
        IReadOnlyDictionary<string, IReadOnlyList<string>> references,
        IReadOnlySet<string> affected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([start]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            if (affected.Contains(current))
            {
                return current;
            }

            foreach (var reference in references.GetValueOrDefault(current, []))
            {
                queue.Enqueue(reference);
            }
        }

        return null;
    }

    private static UnanalysedTestProject Entry(ForeignProject project, bool runs, string reason) => new()
    {
        Name = project.Name,
        Language = project.Language,
        ProjectPath = project.FilePath,
        Assembly = project.AssemblyName,
        Runs = runs,
        Reason = reason,
    };
}
