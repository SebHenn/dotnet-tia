using Tia.Core.Analysis;
using Tia.Core.Caching;

namespace Tia.Workspace;

// Source generators: the ones that did not load, and seeding symbols from generated code that changed.
internal sealed partial class ChangeResolver
{
    /// <summary>
    /// Widens a project whose source generator did not load, when the diff reaches it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What such a generator would have emitted is missing from the graph, and so is every edge
    /// out of it, so the graph cannot say which change that code reads. Treating the whole
    /// project as changed is the one sound answer, the same one given when the generated documents
    /// are not in the compilation at all. Found on the Razor generator: built against a newer
    /// Roslyn than this tool carries, it was skipped without a word, and a change to a page model
    /// selected 0 of 7 tests while the page test broke.
    /// </para>
    /// <para>
    /// The generated code can read the project's own symbols and those of every project it
    /// references, so the project is widened when the diff touches it or anything beneath it,
    /// and not otherwise.
    /// </para>
    /// </remarks>
    private static void WidenUnloadedGenerators(
        LoadedWorkspace workspace,
        Dictionary<string, bool> changedProjects,
        SymbolChangeSet changes)
    {
        var touched = new HashSet<string>(changedProjects.Keys, StringComparer.Ordinal);
        touched.UnionWith(changes.ProjectWide.Where(w => w.WidensProject).Select(w => w.ProjectName));

        if (touched.Count == 0)
        {
            return;
        }

        var byName = workspace.Projects.ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var context in workspace.Projects)
        {
            if (!ReachesAny(context.Name) || UnloadedGenerators.In(context.Project) is not { Count: > 0 } unloaded)
            {
                continue;
            }

            foreach (var generator in unloaded)
            {
                changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
                    $"{context.Name} uses the source generator {generator.Assembly}, which could not be loaded: " +
                    $"{generator.Reason}. What it generates is not in the graph, so a change it reads cannot be traced");
            }
        }

        bool ReachesAny(string root)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([root]);

            while (pending.Count > 0)
            {
                var name = pending.Pop();
                if (!seen.Add(name))
                {
                    continue;
                }

                if (touched.Contains(name))
                {
                    return true;
                }

                if (byName.TryGetValue(name, out var project))
                {
                    foreach (var reference in project.Descriptor.ProjectReferences)
                    {
                        pending.Push(reference);
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Handles a change to a project whose generators actually emit code.
    /// </summary>
    /// <remarks>
    /// The blunt rule is to widen the whole project, because generated trees have no file on disk
    /// to attribute a change to. That is enormously expensive - one generator in a core library
    /// puts the entire solution into full scope - and it is stronger than the risk requires.
    ///
    /// When the generated documents are part of the compilation being analysed, the graph already
    /// carries every edge out of them. The only thing a change to generator input can do that the
    /// graph cannot see is alter the generated code itself, so treating every generated document
    /// as changed models exactly that: whatever depends on generated code is selected, and
    /// whatever does not is left alone. The project-wide widening remains the fallback for when
    /// the generated documents are not in the compilation, where nothing else would be sound.
    /// </remarks>
    private void SeedGeneratedCode(
        ProjectContext context,
        ProjectGraphFragment fragment,
        bool onlyCompiledSource,
        IReadOnlyList<BaseSource> baseSources,
        ChangedSymbolResolver resolver,
        SymbolChangeSet changes,
        CancellationToken cancellationToken)
    {
        if (fragment.GeneratedDocumentCount == 0)
        {
            // No generator in this project emits anything. Every SDK project references several
            // that stay silent unless used, so this is the ordinary case - and asking the project
            // rather than the fragment is what used to make it cost a compilation.
            return;
        }

        var generated = context.GeneratedOutput;

        if (generated.InCompilation.Count == 0)
        {
            changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
                $"{context.Name} runs source generators whose output is not in the analysed compilation, " +
                "so a change to their input cannot be attributed at symbol granularity");
            return;
        }

        // A generator may read an AdditionalFile, an embedded resource, anything. Only a diff made
        // entirely of C# can be replayed faithfully by substituting syntax trees.
        var comparison = onlyCompiledSource
            ? GeneratedCodeDiffer.Compare(context, baseSources, cancellationToken)
            : new GeneratedCodeComparison { Exact = false, Reason = "the diff touches files the generators may read" };

        var toSeed = comparison.Exact ? comparison.Changed : context.GeneratedDocuments;

        if (toSeed.Count == 0)
        {
            changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
                $"{context.Name} runs source generators; re-running them over both revisions shows no generated document changed",
                widensProject: false);
            return;
        }

        var before = changes.Keys.Count;

        foreach (var document in toSeed)
        {
            var model = context.Compilation.GetSemanticModel(document.Tree);
            changes.Merge(resolver.Resolve(model, [WholeFile], context.Name, isNewFile: false, cancellationToken));
        }

        changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
            comparison.Exact
                ? $"{context.Name}: {comparison.Reason}; {changes.Keys.Count - before} generated symbol(s) treated as changed"
                : $"{context.Name} runs source generators and {comparison.Reason}, so all " +
                  $"{toSeed.Count} generated document(s) are treated as changed",
            widensProject: false);
    }
}
