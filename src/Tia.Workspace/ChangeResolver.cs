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
        Dictionary<string, bool> changedProjects,
        List<ChangedFile> unowned)
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

        // Opened out of a submodule, and owned by nothing: widen the submodule as a whole, which is
        // what covered this file before it was opened (#154).
        if (file.Submodule is { } origin)
        {
            WidenSubmodule(workspace, Path.GetFullPath(Path.Combine(repositoryRoot, origin.Path)), origin.Path, changes, fullRunReasons);
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
        // that did would have brought it into the workspace - so as code it has nothing to reach.
        // It can still be read as data, which is what the literals are asked about.
        if (isSource)
        {
            unowned.Add(file);
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
            return;
        }

        unowned.Add(file);

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
    /// Widens the projects a moved package can reach, in place of the full run a manifest otherwise
    /// forces (#171).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this cannot miss: a package can only affect a project that has it in its restored closure,
    /// as a library, an analyzer, a generator or a build file. The assets file lists that closure,
    /// transitive packages and those arriving through a project reference included. A project that
    /// reaches the package through <c>PrivateAssets</c> has a dependency that does list it, and
    /// dependent expansion follows from there. A project that cannot be read might hold the package,
    /// so it forces the full run this replaces.
    /// </para>
    /// <para>
    /// What this leans on: the assets files describing the tree, which is why every command already
    /// requires a restore. One left over from before a package was added could lack it, and that
    /// project would then not widen. A test that launches a project it does not reference stays out
    /// of reach, as it is for any change.
    /// </para>
    /// </remarks>
    private static void ResolvePackageBumps(
        LoadedWorkspace workspace,
        string repositoryRoot,
        IReadOnlyDictionary<string, IReadOnlySet<string>> packageBumps,
        SymbolChangeSet changes,
        List<string> fullRunReasons,
        Dictionary<string, bool> changedProjects)
    {
        var resolved = new Dictionary<string, AssetsReading>(PathComparer);

        foreach (var (path, packages) in packageBumps.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (packages.Count == 0)
            {
                changes.Notes.Add($"{path} changed, but no package version did, so it selects nothing");
                continue;
            }

            if (PackageManifestDiff.IsCentral(path))
            {
                WidenResolving(path, packages);
            }
            else
            {
                WidenOwner(path, packages);
            }
        }

        void WidenOwner(string path, IReadOnlySet<string> packages)
        {
            var absolute = Path.GetFullPath(Path.Combine(repositoryRoot, path));
            var owners = workspace.Projects.Where(p => PathComparer.Equals(p.Descriptor.FilePath, absolute)).ToList();

            if (owners.Count == 0)
            {
                fullRunReasons.Add($"{path} changed - it is not a project this analysis loaded, so what depends on it is unknown");
                return;
            }

            foreach (var owner in owners)
            {
                Widen(owner.Name, $"{path} moved {List(packages)}; what a package does is outside the symbol graph, so {owner.Name} and everything referencing it are impacted");
            }
        }

        void WidenResolving(string path, IReadOnlySet<string> packages)
        {
            var anyResolves = false;

            foreach (var context in workspace.Projects)
            {
                if (Holding(context.Name, context.Descriptor.FilePath, path, packages) is { Count: > 0 } held)
                {
                    anyResolves = true;
                    Widen(context.Name, $"{path} moved {List(held)}, which {context.Name} resolves; what a package does is outside the symbol graph, so it and everything referencing it are impacted");
                }
            }

            foreach (var foreign in workspace.ForeignProjects)
            {
                if (Holding(foreign.Name, foreign.FilePath, path, packages) is { Count: > 0 } held)
                {
                    anyResolves = true;
                    changes.AddProjectWide(foreign.Name, ProjectWideCause.ForeignLanguage,
                        $"{path} moved {List(held)}, which {foreign.Name} resolves; it is a {foreign.Language} project this engine does not analyse, so it and everything referencing it are impacted");
                }
            }

            if (!anyResolves)
            {
                changes.Notes.Add($"{path} moved {List(packages)}, which no project resolves, so it selects nothing");
            }
        }

        List<string>? Holding(string projectName, string projectFile, string path, IReadOnlySet<string> packages)
        {
            if (!resolved.TryGetValue(projectFile, out var reading))
            {
                reading = ProjectAssets.Read(projectFile);
                resolved[projectFile] = reading;
            }

            if (reading.Packages is not { } closure)
            {
                fullRunReasons.Add($"{path} moved {List(packages)}, and whether {projectName} resolves it is unknown: {reading.Problem}");
                return null;
            }

            return [.. packages.Where(closure.Contains)];
        }

        void Widen(string projectName, string detail)
        {
            changedProjects[projectName] = false;
            changes.AddProjectWide(projectName, ProjectWideCause.PackageVersion, detail);
        }

        static string List(IEnumerable<string> packages) => string.Join(", ", packages.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Seeds the code whose string literals name a changed file no project owns, and says which
    /// files nothing names (#168).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each literal's line is resolved exactly as a changed line would be, because that is the
    /// claim: what that declaration does may have changed. So a property's readers follow, a
    /// constant widens as a changed constant does, and an attribute's line reaches what it annotates.
    /// It cannot cause a miss where there was none, since it only ever adds to the changed set.
    /// </para>
    /// <para>
    /// The old path counts as well as the new one: code still naming a renamed file is what the
    /// rename breaks. Every document is read, but only when the diff holds such a file, and only one
    /// holding the file's name is parsed.
    /// </para>
    /// </remarks>
    private static void ResolveUnowned(
        LoadedWorkspace workspace,
        ImpactGraph graph,
        string repositoryRoot,
        List<ChangedFile> unowned,
        FragmentIndex byFile,
        IReadOnlyDictionary<string, ProjectGraphFragment> fragments,
        SymbolChangeSet changes,
        List<string> fullRunReasons,
        CancellationToken cancellationToken)
    {
        if (unowned.Count == 0)
        {
            return;
        }

        var paths = unowned
            .SelectMany(f => new[] { f.Path, f.OldPath })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var fileNames = paths.Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var named = new HashSet<string>(StringComparer.Ordinal);

        // A multi-targeted project lists every file once per framework. Whether a file holds the name
        // at all does not depend on the framework, though which of its literals are live does.
        var withoutTheName = new HashSet<string>(PathComparer);

        foreach (var context in workspace.Projects)
        {
            foreach (var document in context.Project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (document.FilePath is not { } documentPath || withoutTheName.Contains(documentPath))
                {
                    continue;
                }

                var text = document.GetTextAsync(cancellationToken).GetAwaiter().GetResult();
                var content = text.ToString();
                if (!fileNames.Exists(name => content.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    withoutTheName.Add(documentPath);
                    continue;
                }

                var tree = document.GetSyntaxTreeAsync(cancellationToken).GetAwaiter().GetResult();
                if (tree is null)
                {
                    continue;
                }

                var lines = NamedFileLiterals.Find(tree, paths, cancellationToken);
                if (lines.Count == 0)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(repositoryRoot, documentPath).Replace('\\', '/');

                if (!fragments.TryGetValue(context.Name, out var fragment))
                {
                    fullRunReasons.Add($"{context.Name} produced no graph fragment, so what {relative} declares is unknown");
                    continue;
                }

                changes.Merge(DeclarationSiteResolver.Resolve(
                    byFile.SitesIn(fragment, documentPath),
                    graph,
                    [.. lines.Select(l => new LineRange(l.Line, l.Line))],
                    context.Name,
                    documentPath,
                    text.Lines.Count,
                    isNewFile: false,
                    () => tree,
                    cancellationToken));

                foreach (var line in lines)
                {
                    named.Add(line.Path);
                    changes.AddProjectWide(
                        context.Name,
                        ProjectWideCause.ContentFile,
                        FormattableString.Invariant($"{line.Path} is in no project, but {relative} line {line.Line} names it; what that declaration reaches is impacted"),
                        widensProject: false);
                }
            }
        }

        var silent = unowned
            .Where(f => !named.Contains(f.Path) && (f.OldPath is null || !named.Contains(f.OldPath)))
            .Select(f => f.Path)
            .ToList();

        if (silent.Count > 0)
        {
            changes.Notes.Add(UnownedNote(silent));
        }
    }

    /// <summary>
    /// A selection of zero has to say what it ignored, since a README and a file read by a path no
    /// literal spells look the same from here. Capped: a documentation commit can touch hundreds.
    /// </summary>
    private static string UnownedNote(List<string> paths)
    {
        const int Shown = 5;

        var listed = string.Join(", ", paths.Take(Shown));
        if (paths.Count > Shown)
        {
            listed += FormattableString.Invariant($" and {paths.Count - Shown} more");
        }

        return paths.Count == 1
            ? $"{listed} is in no project and no C# string names it, so it selects nothing"
            : $"{listed} are in no project and no C# string names them, so they select nothing";
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

    /// <summary>Whether a loaded project, C# or foreign, lives at or below <paramref name="directory"/>.</summary>
    private static bool HoldsLoadedProject(LoadedWorkspace workspace, string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return workspace.Projects.Any(p => (p.Descriptor.Directory + Path.DirectorySeparatorChar).StartsWith(prefix, PathComparison)) ||
               workspace.ForeignProjects.Any(p => (p.Directory + Path.DirectorySeparatorChar).StartsWith(prefix, PathComparison));
    }

    /// <summary>Whether any project compiles a C# file in the same folder as <paramref name="absolutePath"/>.</summary>
    private static bool HasCompiledNeighbour(LoadedWorkspace workspace, string absolutePath)
    {
        var directory = Path.GetDirectoryName(absolutePath) + Path.DirectorySeparatorChar;
        return workspace.Projects.Any(p => p.Project.Documents.Any(d =>
            d.FilePath is { } path &&
            path.StartsWith(directory, PathComparison) &&
            path.IndexOf(Path.DirectorySeparatorChar, directory.Length) < 0));
    }

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
