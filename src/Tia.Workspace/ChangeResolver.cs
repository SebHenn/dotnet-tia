using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Tia.Core.Analysis;
using Tia.Core.Caching;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Safety;

namespace Tia.Workspace;

/// <summary>
/// Turns a git diff into the set of symbols it changed.
/// </summary>
/// <remarks>
/// This is where a wrong answer is cheapest to produce and hardest to see. Everything downstream
/// takes the change set on trust, so a path that resolves against the wrong root, a hunk that maps
/// to no declaration, or a rename read as an unrelated add all end the same way: fewer seeds, a
/// smaller selection, and no error anywhere. Each of those has happened, and the widenings this
/// records are how the report says so out loud instead.
/// </remarks>
internal sealed class ChangeResolver(PhaseClock? clock = null)
{
    /// <remarks>
    /// Diff paths are relative to the *git root*, which is not necessarily the directory being
    /// analysed: a solution can live in a subdirectory of its repository. Resolving them against
    /// the analysed directory instead produced paths that matched no document at all, so nothing
    /// was ever selected - a silent miss rather than an error.
    /// </remarks>
    public SymbolChangeSet Resolve(
        LoadedWorkspace workspace,
        IGitClient git,
        DiffResult diff,
        ImpactGraph graph,
        IReadOnlyDictionary<string, ProjectGraphFragment> fragments,
        out List<string> compilationErrors,
        CancellationToken cancellationToken)
    {
        compilationErrors = [];
        var changes = new SymbolChangeSet();
        // Only ever handed generator output; every file on disk resolves from stored positions.
        var resolver = new ChangedSymbolResolver { GeneratedDocuments = true };
        var typeIndexes = new Dictionary<string, SourceTypeIndex>(StringComparer.Ordinal);
        var byFile = new FragmentIndex();
        var oldContents = FetchOldSides(git, diff, clock, cancellationToken);

        // Collected as we go, then used once per project: recomputing generated output is a
        // per-project operation, not a per-file one. True while everything that changed in the
        // project is code it compiles, which is the only kind of change that can be replayed.
        var changedProjects = new Dictionary<string, bool>(StringComparer.Ordinal);
        var baseSourcesByProject = new Dictionary<string, List<BaseSource>>(StringComparer.Ordinal);
        var linked = new LinkedItems(workspace, git.RepositoryRoot, clock);

        foreach (var file in diff.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var absolute = Path.GetFullPath(Path.Combine(git.RepositoryRoot, file.Path));

            if (!file.IsCSharp)
            {
                // A moved submodule is one entry in the diff, its gitlink, and none of the files
                // inside it are. The gitlink is the directory the submodule is checked out in, which
                // no ordinary changed file can be; it matched no project, since a project's directory
                // is a prefix of its files and not equal to them, and so a bump of a vendored
                // library ran nothing (#124).
                if (Directory.Exists(absolute))
                {
                    if (FindOwningProject(workspace, absolute) is { } container)
                    {
                        changedProjects[container.Name] = false;
                    }

                    WidenSubmodule(workspace, absolute, file.Path, changes, compilationErrors);
                    continue;
                }

                if (ContentFileRules.IsWideningContent(file.Path))
                {
                    WidenContent(workspace, linked, git.RepositoryRoot, file, absolute, isSource: false, changes, compilationErrors, changedProjects);
                }

                continue;
            }

            // A .cs file no project compiles is data, whatever its extension says: an analyzer
            // test's input, an embedded sample, a Verify snapshot. Read as code, an added one
            // seeded nothing and a modified one was matched by type name against the project's
            // real types, which seeded whatever happened to share a name (#123).
            var compiled = file.ExistsOnNewSide
                ? workspace.Solution.GetDocumentIdsWithFilePath(absolute).Select(id => workspace.Solution.GetDocument(id)).OfType<Document>().ToList()
                : [];
            var uncompiled = file.ExistsOnNewSide && compiled.Count == 0;

            if (uncompiled)
            {
                WidenContent(workspace, linked, git.RepositoryRoot, file, absolute, isSource: true, changes, compilationErrors, changedProjects);

                // Only a rename can have taken a file out of the compilation that was in it before,
                // and only its old side can say what that file declared.
                if (file.OldPath is null)
                {
                    continue;
                }
            }

            // Read before the new side is, because whether the new side is worth reading at all
            // depends on what the old side said. Fetched up front rather than here: see
            // FetchOldSides.
            var oldPath = file.OldSidePath;
            var oldContent = oldPath is null ? null : oldContents.GetValueOrDefault(oldPath);

            // New side. Every project the file belongs to, not just the first: a multi-targeted
            // project compiles the same file once per framework, with different preprocessor
            // symbols, so a change inside `#if NET8_0` is real code in one of them and disabled
            // text in the others. Reading only the first meant whichever framework the workspace
            // happened to list first decided whether the change existed at all.
            var triviaOnlyEverywhere = true;
            var sawAnyTree = false;
            var reportedCompileError = false;

            foreach (var document in compiled)
            {
                var context = workspace.Projects.FirstOrDefault(p => p.Project.Id == document.Project.Id);

                if (context is null)
                {
                    continue;
                }

                // A project the graph produced no fragment for is one whose declarations are
                // unknown, and seeding nothing for a file in it would be a silent miss - the exact
                // shape of defect this whole path is built to avoid. It cannot happen today, since
                // every project gets a fragment; if it ever does, it has to be loud.
                if (!fragments.TryGetValue(context.Name, out var fragment))
                {
                    compilationErrors.Add(
                        $"{context.Name} produced no graph fragment, so what {file.Path} declares is unknown");
                    continue;
                }

                sawAnyTree = true;

                // The document's own text and parse options, neither of which needs the project
                // compiled. Producing a compilation to read one file's declarations back was the
                // largest single cost in a warm run.
                var text = document.GetTextAsync(cancellationToken).GetAwaiter().GetResult();

                var triviaStarted = Stopwatch.GetTimestamp();
                var triviaOnly = IsTriviaOnly(fragment, file, oldContent, text, document.Project.ParseOptions, cancellationToken);
                clock?.Record(nameof(PhaseTimings.TriviaCheckSeconds), triviaStarted);

                if (triviaOnly)
                {
                    continue;
                }

                triviaOnlyEverywhere = false;

                // Once per file, not once per target framework.
                if (!reportedCompileError && byFile.ErrorIn(fragment, absolute) is { } fileError)
                {
                    compilationErrors.Add($"{file.Path} does not compile ({fileError})");
                    reportedCompileError = true;
                }

                changes.Merge(DeclarationSiteResolver.Resolve(
                    byFile.SitesIn(fragment, absolute),
                    graph,
                    file.NewLines,
                    context.Name,
                    absolute,
                    text.Lines.Count,
                    isNewFile: file.Kind == FileChangeKind.Added,
                    () => document.GetSyntaxTreeAsync(cancellationToken).GetAwaiter().GetResult(),
                    cancellationToken));
            }

            // Only when every project that compiles the file agrees. One framework's disabled
            // region is another's live code.
            if (sawAnyTree && triviaOnlyEverywhere)
            {
                changes.Notes.Add($"{file.Path} changed only comments or formatting; no token moved, so it seeds nothing");
                continue;
            }

            // Old side: deletions and renames are invisible in the new tree, and a deleted
            // override or interface implementation changes behaviour without touching any caller.
            if (!uncompiled)
            {
                RecordSourceChange(workspace, compiled, absolute, oldContent, changedProjects, baseSourcesByProject);
            }

            if (oldPath is null || oldContent is null)
            {
                continue;
            }

            // Falls back to whichever project holds the file on the new side. Any of them will do:
            // the old side is resolved against a type index, and a multi-targeted project's
            // frameworks declare the same types.
            var newSideOwner = compiled.FirstOrDefault()?.Project.Id;

            var oldOwner = FindOwningProject(workspace, Path.GetFullPath(Path.Combine(git.RepositoryRoot, oldPath)))
                           ?? (newSideOwner is not null
                               ? workspace.Projects.FirstOrDefault(p => p.Project.Id == newSideOwner)?.Descriptor
                               : null);

            if (oldOwner is null)
            {
                continue;
            }

            if (!typeIndexes.TryGetValue(oldOwner.Name, out var index))
            {
                index = SourceTypeIndex.FromGraph(graph, oldOwner.Name, cancellationToken);
                typeIndexes[oldOwner.Name] = index;
            }

            var oldSideStarted = Stopwatch.GetTimestamp();
            changes.Merge(new OldSideResolver(index).Resolve(oldContent, file.OldLines, oldOwner.Name, oldPath, cancellationToken));
            clock?.Record(nameof(PhaseTimings.OldSideResolveSeconds), oldSideStarted);
        }

        if (linked.Unevaluated.Count > 0)
        {
            changes.Notes.Add(
                $"MSBuild could not evaluate {string.Join(", ", linked.Unevaluated)}, so whether it embeds or copies " +
                "a changed file from outside its folder is unknown");
        }

        foreach (var context in workspace.Projects)
        {
            if (changedProjects.TryGetValue(context.Name, out var onlyCompiledSource) &&
                fragments.TryGetValue(context.Name, out var fragment))
            {
                SeedGeneratedCode(
                    context,
                    fragment,
                    onlyCompiledSource,
                    baseSourcesByProject.GetValueOrDefault(context.Name, []),
                    resolver,
                    changes,
                    cancellationToken);
            }
        }

        return changes;
    }

    /// <summary>
    /// Per-file views of each fragment, built once and only for the fragments a diff reaches.
    /// </summary>
    /// <remarks>
    /// A fragment stores its declarations as one flat list, which is the right shape on disk and
    /// the wrong one here: scanning it per changed file is the project's whole declaration count
    /// times the number of files that changed in it.
    /// </remarks>
    private sealed class FragmentIndex
    {
        private readonly Dictionary<string, Dictionary<string, List<DeclarationSite>>> _sites = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Dictionary<string, string>> _errors = new(StringComparer.Ordinal);

        public IReadOnlyList<DeclarationSite> SitesIn(ProjectGraphFragment fragment, string absolutePath)
        {
            if (!_sites.TryGetValue(fragment.ProjectName, out var byPath))
            {
                byPath = new Dictionary<string, List<DeclarationSite>>(PathComparer);
                foreach (var site in fragment.Declarations)
                {
                    if (!byPath.TryGetValue(site.FilePath, out var list))
                    {
                        list = [];
                        byPath[site.FilePath] = list;
                    }

                    list.Add(site);
                }

                _sites[fragment.ProjectName] = byPath;
            }

            return byPath.GetValueOrDefault(absolutePath) ?? (IReadOnlyList<DeclarationSite>)[];
        }

        public string? ErrorIn(ProjectGraphFragment fragment, string absolutePath)
        {
            if (!_errors.TryGetValue(fragment.ProjectName, out var byPath))
            {
                byPath = new Dictionary<string, string>(PathComparer);
                foreach (var error in fragment.FileErrors)
                {
                    byPath[error.FilePath] = error.Error;
                }

                _errors[fragment.ProjectName] = byPath;
            }

            return byPath.GetValueOrDefault(absolutePath);
        }
    }

    /// <summary>
    /// Every changed file's content at the base revision, read concurrently.
    /// </summary>
    /// <remarks>
    /// <c>git show</c> answers in about a millisecond and costs about thirty to start, so a
    /// sixteen-file change spent half a second waiting for processes rather than for git. Read one
    /// after another from inside the loop this was half the time change resolution took that was
    /// not compiling something.
    ///
    /// Concurrent rather than batched through <c>cat-file --batch</c>: that protocol frames each
    /// object by its size in <em>bytes</em>, and everything here is decoded text, so splitting the
    /// stream correctly would mean re-encoding to count. Spawning the same processes in parallel
    /// removes the same wait without inventing a parser. Each call is read-only and takes no index
    /// lock, so they do not contend.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> FetchOldSides(
        IGitClient git,
        DiffResult diff,
        PhaseClock? clock,
        CancellationToken cancellationToken)
    {
        var paths = diff.Files
            .Where(f => f.IsCSharp)
            .Select(f => f.OldSidePath)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var contents = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        if (paths.Count == 0)
        {
            return contents;
        }

        var started = Stopwatch.GetTimestamp();

        Parallel.ForEach(paths, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        }, path =>
        {
            // Absent at the base revision is an ordinary answer - an added file has no old side -
            // and is recorded by leaving the entry out, which is what the caller already read a
            // null return as.
            if (git.ShowFile(diff.BaseCommit, path) is { } content)
            {
                contents[path] = content;
            }
        });

        clock?.Record(nameof(PhaseTimings.OldSideFetchSeconds), started);
        return contents;
    }

    /// <summary>
    /// Records a changed C# file, with its base-revision content, against every project that
    /// compiles it - or, for a deleted file, the project whose folder held it.
    /// </summary>
    /// <remarks>
    /// The compiling projects rather than the folder's: a file linked into a project from elsewhere
    /// is that project's generator input too, and recording it against the folder left the
    /// generators of the project that actually compiles it never re-run (#123).
    /// </remarks>
    private static void RecordSourceChange(
        LoadedWorkspace workspace,
        IReadOnlyList<Document> compiled,
        string absolutePath,
        string? baseContent,
        Dictionary<string, bool> changedProjects,
        Dictionary<string, List<BaseSource>> baseSourcesByProject)
    {
        IEnumerable<string> owners = compiled.Count > 0
            ? compiled.Select(d => workspace.Projects.FirstOrDefault(p => p.Project.Id == d.Project.Id)?.Name).OfType<string>()
            : FindOwningProject(workspace, absolutePath) is { } owner ? [owner.Name] : [];

        foreach (var name in owners.Distinct(StringComparer.Ordinal))
        {
            changedProjects.TryAdd(name, true);

            if (!baseSourcesByProject.TryGetValue(name, out var sources))
            {
                sources = [];
                baseSourcesByProject[name] = sources;
            }

            sources.Add(new BaseSource(absolutePath, baseContent));
        }
    }

    /// <summary>
    /// Widens every project that ships a file the compiler does not read as code: the project whose
    /// folder holds it, and any that link it from elsewhere (#123). Each is recorded as having
    /// changed in a way its generators cannot be replayed over, since a generator may read it.
    /// </summary>
    /// <remarks>
    /// The workspace's own view is asked first, because it costs a lookup. MSBuild is asked only
    /// when nothing else owns the file, because that costs an evaluation; a file a folder owns and
    /// another project also embeds widens the folder's project, which its dependents follow.
    /// </remarks>
    private static void WidenContent(
        LoadedWorkspace workspace,
        LinkedItems linked,
        string repositoryRoot,
        ChangedFile file,
        string absolutePath,
        bool isSource,
        SymbolChangeSet changes,
        List<string> fullRunReasons,
        Dictionary<string, bool> changedProjects)
    {
        var what = isSource ? "is C# that no project compiles" : "is not source";
        const string why = "nothing in the symbol graph connects it to the tests that read it";
        var widened = new HashSet<string>(StringComparer.Ordinal);

        if (FindOwningProject(workspace, absolutePath) is { } owner)
        {
            Widen(owner, $"{file.Path} {what}; {why}");
        }

        foreach (var link in linked.InWorkspace(absolutePath))
        {
            Widen(link.Project, $"{file.Path} {what}, and {link.Project.Name} {link.How}; {why}");
        }

        if (widened.Count == 0)
        {
            foreach (var link in linked.Evaluated(absolutePath, file.ExistsOnNewSide))
            {
                Widen(link.Project, $"{file.Path} {what}, and {link.Project.Name} {link.How} from outside its folder; {why}");
            }
        }

        if (widened.Count > 0)
        {
            return;
        }

        // No C# project owns it. Before this, that ended the matter and the file widened
        // nothing - so a C# test project exercising an F# or VB library selected zero tests
        // for a change to that library. A foreign project contributes no symbols, so the
        // only sound answer is to widen it and let dependent expansion do the rest.
        if (FindOwningForeignProject(workspace, absolutePath) is { } foreignOwner)
        {
            changes.AddProjectWide(foreignOwner.Name, ProjectWideCause.ForeignLanguage,
                $"{file.Path} belongs to {foreignOwner.Name}, a {foreignOwner.Language} project this engine " +
                "does not analyse; it contributes no symbols, so it and everything referencing it are impacted");
            return;
        }

        // C# in a project the workspace did not load is code no loaded project references - one
        // that did would have brought it into the workspace - so there is nothing for it to reach.
        if (isSource)
        {
            changes.Notes.Add($"{file.Path} is C# that no loaded project compiles or includes, so it seeds nothing");
            return;
        }

        // The backstop, which closes the class rather than the instance: a project type the
        // workspace skipped outright never appears in it at all, so there is nothing to
        // widen and no way to know what depends on it. A file sitting inside a project that
        // exists on disk but not in the workspace is exactly that case.
        if (UnloadedProjectDirectory(repositoryRoot, absolutePath) is { } projectFile)
        {
            fullRunReasons.Add(
                $"{file.Path} belongs to {Path.GetFileName(projectFile)}, which the workspace did not load - " +
                "nothing can connect it to a test, so the whole suite runs");
        }

        void Widen(ProjectDescriptor project, string detail)
        {
            if (!widened.Add(project.Name))
            {
                return;
            }

            changedProjects[project.Name] = false;
            changes.AddProjectWide(project.Name, ProjectWideCause.ContentFile, detail);
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

    private static readonly LineRange WholeFile = new(1, int.MaxValue);

    /// <summary>
    /// Whether this file's change moved no token, in which case it seeds nothing.
    /// </summary>
    /// <remarks>
    /// Only for a modified file: an add or a delete is not a formatting change however its tokens
    /// compare. And only when the project's generators emit nothing, because a generator reads the
    /// syntax tree including its trivia - an attribute's doc comment, a marker in a comment - so in
    /// a project with a live generator a comment really can change what is compiled.
    /// </remarks>
    private static bool IsTriviaOnly(
        ProjectGraphFragment fragment,
        ChangedFile file,
        string? oldContent,
        SourceText text,
        ParseOptions? parseOptions,
        CancellationToken cancellationToken) =>
        oldContent is not null &&
        file.Kind == FileChangeKind.Modified &&
        fragment.GeneratedDocumentCount == 0 &&
        TriviaOnlyChange.Applies(oldContent, text.ToString(), parseOptions, cancellationToken);

    private static ProjectDescriptor? FindOwningProject(LoadedWorkspace workspace, string absolutePath)
    {
        ProjectDescriptor? best = null;
        var bestLength = -1;

        foreach (var context in workspace.Projects)
        {
            var directory = context.Descriptor.Directory;
            if (directory.Length <= bestLength)
            {
                continue;
            }

            if (absolutePath.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison))
            {
                best = context.Descriptor;
                bestLength = directory.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Widens every loaded project in the moved submodule at <paramref name="absolutePath"/>, and the
    /// project it sits in, whose default globs compile what is below it. When none is loaded, the
    /// submodule holds nothing the graph can trace and the whole suite runs: it may be compiled by
    /// a project that links its files, imported by a props file, or read at run time.
    /// </summary>
    /// <remarks>
    /// An uninitialised submodule is an empty directory, holds no loaded project, and runs
    /// everything, which is the only sound answer when what changed cannot be seen.
    /// </remarks>
    private static void WidenSubmodule(
        LoadedWorkspace workspace,
        string absolutePath,
        string relativePath,
        SymbolChangeSet changes,
        List<string> fullRunReasons)
    {
        var prefix = absolutePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool Inside(string directory) => (directory + Path.DirectorySeparatorChar).StartsWith(prefix, PathComparison);

        var widened = workspace.Projects.Select(p => p.Descriptor).Where(d => Inside(d.Directory)).Select(d => d.Name)
            .Concat(workspace.ForeignProjects.Where(p => Inside(p.Directory)).Select(p => p.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var name in widened)
        {
            changes.AddProjectWide(name, ProjectWideCause.Submodule,
                $"{relativePath} is a submodule that moved, and {name} lives in it; the diff does not say what changed inside");
        }

        if (FindOwningProject(workspace, absolutePath) is { } container)
        {
            changes.AddProjectWide(container.Name, ProjectWideCause.Submodule,
                $"{relativePath} is a submodule that moved inside {container.Name}; the diff does not say what changed inside");
            return;
        }

        if (widened.Count == 0)
        {
            fullRunReasons.Add(
                $"{relativePath} is a submodule that moved, and no loaded project lives in it - whatever it holds cannot be " +
                "traced to a test, so the whole suite runs");
        }
    }

    /// <summary>
    /// The deepest foreign project whose directory contains <paramref name="absolutePath"/>, or null.
    /// Deepest, like <see cref="FindOwningProject"/>: nested projects are ordinary, and the nearest
    /// one is the one that ships the file.
    /// </summary>
    private static ForeignProject? FindOwningForeignProject(LoadedWorkspace workspace, string absolutePath)
    {
        ForeignProject? best = null;
        var bestLength = -1;

        foreach (var project in workspace.ForeignProjects)
        {
            if (project.Directory.Length > bestLength &&
                absolutePath.StartsWith(project.Directory + Path.DirectorySeparatorChar, PathComparison))
            {
                best = project;
                bestLength = project.Directory.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// The project file of a project that exists on disk at or above <paramref name="absolutePath"/>
    /// but that the workspace never listed, or null when the file belongs to no project at all.
    /// </summary>
    /// <remarks>
    /// Only reached once every loaded project, C# or foreign, has been ruled out - so anything this
    /// finds is a project <c>SkipUnrecognizedProjects</c> dropped, and the caller has no way to know
    /// what depends on it. Returning null is the common and cheap case: a repository's <c>docs/</c>
    /// and <c>.github/</c> sit above every project file and still select nothing.
    /// </remarks>
    private static string? UnloadedProjectDirectory(string repositoryRoot, string absolutePath)
    {
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(absolutePath);

        while (directory is not null)
        {
            var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar);
            var found = Directory.EnumerateFiles(trimmed, "*.*proj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => Path.GetExtension(f).EndsWith("proj", StringComparison.OrdinalIgnoreCase));

            if (found is not null)
            {
                return found;
            }

            if (string.Equals(trimmed, root, PathComparison))
            {
                return null;
            }

            directory = Path.GetDirectoryName(trimmed);
        }

        return null;
    }

    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static StringComparer PathComparer =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
