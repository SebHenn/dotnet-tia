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
internal sealed partial class ChangeResolver(PhaseClock? clock = null)
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
        IReadOnlyDictionary<string, IReadOnlySet<string>> packageBumps,
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
        var unowned = new List<ChangedFile>();

        // A submodule opened into its files (#154) is only as good as the projects that can claim
        // them. One that holds no loaded project is widened as a whole, as before: whatever it
        // holds may be linked, imported or read at run time, and nothing would own its files.
        var opaque = diff.Files
            .Select(f => f.Submodule?.Path)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(path => !HoldsLoadedProject(workspace, Path.GetFullPath(Path.Combine(git.RepositoryRoot, path))))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var path in opaque)
        {
            WidenSubmodule(workspace, Path.GetFullPath(Path.Combine(git.RepositoryRoot, path)), path, changes, compilationErrors);
        }

        foreach (var file in diff.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.Submodule is { } origin && opaque.Contains(origin.Path))
            {
                continue;
            }

            if (packageBumps.ContainsKey(file.Path))
            {
                continue;
            }

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
                    WidenContent(workspace, linked, git.RepositoryRoot, file, absolute, isSource: false, changes, compilationErrors, changedProjects, unowned);
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
                WidenContent(workspace, linked, git.RepositoryRoot, file, absolute, isSource: true, changes, compilationErrors, changedProjects, unowned);

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

            // An old side no project compiled declared nothing, and matching it by type name against
            // the project's real types selected whatever shared a name (#156). A deleted one was
            // content, and widens as content does. Only for a file no project compiles now, and
            // only when no compiled C# sits beside its old path: that is almost always code, and
            // the evaluation that would say for certain costs a quarter of a second a project.
            // Unsure in either direction keeps the match, which can only select more.
            var oldAbsolute = Path.GetFullPath(Path.Combine(git.RepositoryRoot, oldPath));
            if (compiled.Count == 0 &&
                !HasCompiledNeighbour(workspace, oldAbsolute) &&
                linked.Compiles(oldAbsolute, FindOwningProject(workspace, oldAbsolute)) == false)
            {
                if (!file.ExistsOnNewSide)
                {
                    WidenContent(workspace, linked, git.RepositoryRoot, file, absolute, isSource: true, changes, compilationErrors, changedProjects, unowned);
                }

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

        ResolveUnowned(workspace, graph, git.RepositoryRoot, unowned, byFile, fragments, changes, compilationErrors, cancellationToken);
        ResolvePackageBumps(workspace, git.RepositoryRoot, packageBumps, changes, compilationErrors, changedProjects);

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

        WidenUnloadedGenerators(workspace, changedProjects, changes);

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
            .Where(f => f.IsCSharp && f.OldSidePath is not null)
            .DistinctBy(f => f.OldSidePath, StringComparer.Ordinal)
            .ToList();

        // A file opened out of a submodule has its old side in the submodule's history (#154).
        var submodules = new System.Collections.Concurrent.ConcurrentDictionary<string, IGitClient?>(StringComparer.Ordinal);

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
        }, file =>
        {
            var path = file.OldSidePath!;
            var content = file.Submodule is { } origin
                ? submodules.GetOrAdd(origin.Path, git.OpenSubmodule)?.ShowFile(origin.OldCommit, origin.Inner(path))
                : git.ShowFile(diff.BaseCommit, path);

            // Absent at the base revision is an ordinary answer - an added file has no old side -
            // and is recorded by leaving the entry out, which is what the caller already read a
            // null return as.
            if (content is not null)
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

    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static StringComparer PathComparer =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
