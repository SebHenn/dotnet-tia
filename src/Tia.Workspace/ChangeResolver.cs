using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;
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
internal sealed class ChangeResolver(Action<string> log)
{
    private readonly Action<string> _log = log;

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
        out List<string> compilationErrors,
        CancellationToken cancellationToken)
    {
        compilationErrors = [];
        var changes = new SymbolChangeSet();
        var resolver = new ChangedSymbolResolver();
        var typeIndexes = new Dictionary<string, SourceTypeIndex>(StringComparer.Ordinal);

        // Collected as we go, then used once per project: recomputing generated output is a
        // per-project operation, not a per-file one.
        var changedByProject = new Dictionary<string, List<ChangedFile>>(StringComparer.Ordinal);
        var baseSourcesByProject = new Dictionary<string, List<BaseSource>>(StringComparer.Ordinal);

        foreach (var file in diff.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var absolute = Path.GetFullPath(Path.Combine(git.RepositoryRoot, file.Path));

            if (!file.IsCSharp)
            {
                // Recorded even when it does not widen: a generator may read it, which is what
                // makes recomputing that project's generated output unreliable.
                RecordProjectChange(workspace, absolute, file, null, changedByProject, baseSourcesByProject);

                if (!ContentFileRules.IsWideningContent(file.Path))
                {
                    continue;
                }

                var owner = FindOwningProject(workspace, absolute);
                if (owner is not null)
                {
                    changes.AddProjectWide(owner.Name, ProjectWideCause.ContentFile,
                        $"{file.Path} is not source; nothing in the symbol graph connects it to the tests that read it");
                }

                continue;
            }

            // Fetched before the new side is read, because whether the new side is worth reading
            // at all depends on what the old side said.
            var oldPath = file.OldSidePath;
            var oldContent = oldPath is null ? null : git.ShowFile(diff.BaseCommit, oldPath);

            // New side. Every project the file belongs to, not just the first: a multi-targeted
            // project compiles the same file once per framework, with different preprocessor
            // symbols, so a change inside `#if NET8_0` is real code in one of them and disabled
            // text in the others. Reading only the first meant whichever framework the workspace
            // happened to list first decided whether the change existed at all.
            var triviaOnlyEverywhere = true;
            var sawAnyTree = false;
            var reportedCompileError = false;

            foreach (var documentId in file.ExistsOnNewSide
                         ? workspace.Solution.GetDocumentIdsWithFilePath(absolute)
                         : [])
            {
                var document = workspace.Solution.GetDocument(documentId)!;
                var context = workspace.Projects.FirstOrDefault(p => p.Project.Id == document.Project.Id);
                var tree = context?.Compilation.SyntaxTrees.FirstOrDefault(t => PathsEqual(t.FilePath, absolute));

                if (context is null || tree is null)
                {
                    continue;
                }

                sawAnyTree = true;

                if (IsTriviaOnly(context, file, oldContent, tree, cancellationToken))
                {
                    continue;
                }

                triviaOnlyEverywhere = false;

                var model = context.Compilation.GetSemanticModel(tree);

                foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken))
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        // Once per file, not once per target framework.
                        if (!reportedCompileError)
                        {
                            compilationErrors.Add($"{file.Path} does not compile ({diagnostic.Id}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)})");
                            reportedCompileError = true;
                        }

                        break;
                    }
                }

                changes.Merge(resolver.Resolve(model, file.NewLines, context.Name,
                    isNewFile: file.Kind == FileChangeKind.Added, cancellationToken));
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
            RecordProjectChange(workspace, absolute, file, oldContent, changedByProject, baseSourcesByProject);

            if (oldPath is null || oldContent is null)
            {
                continue;
            }

            // Falls back to whichever project holds the file on the new side. Any of them will do:
            // the old side is resolved against a type index, and a multi-targeted project's
            // frameworks declare the same types.
            var newSideOwner = workspace.Solution.GetDocumentIdsWithFilePath(absolute).FirstOrDefault();

            var oldOwner = FindOwningProject(workspace, Path.GetFullPath(Path.Combine(git.RepositoryRoot, oldPath)))
                           ?? (newSideOwner is not null
                               ? workspace.Projects.FirstOrDefault(p => p.Project.Id == workspace.Solution.GetDocument(newSideOwner)!.Project.Id)?.Descriptor
                               : null);

            if (oldOwner is null)
            {
                continue;
            }

            var ownerContext = workspace.Projects.First(p => p.Name == oldOwner.Name);
            if (!typeIndexes.TryGetValue(oldOwner.Name, out var index))
            {
                index = SourceTypeIndex.Build(ownerContext.Compilation, cancellationToken);
                typeIndexes[oldOwner.Name] = index;
            }

            changes.Merge(new OldSideResolver(index).Resolve(oldContent, file.OldLines, oldOwner.Name, oldPath, cancellationToken));
        }

        foreach (var context in workspace.Projects)
        {
            if (changedByProject.TryGetValue(context.Name, out var changedInProject))
            {
                SeedGeneratedCode(
                    context,
                    changedInProject,
                    baseSourcesByProject.GetValueOrDefault(context.Name, []),
                    resolver,
                    changes,
                    cancellationToken);
            }
        }

        return changes;
    }

    /// <summary>Files that changed in each project, with their base-revision content.</summary>
    private static void RecordProjectChange(
        LoadedWorkspace workspace,
        string absolutePath,
        ChangedFile file,
        string? baseContent,
        Dictionary<string, List<ChangedFile>> changedByProject,
        Dictionary<string, List<BaseSource>> baseSourcesByProject)
    {
        var owner = FindOwningProject(workspace, absolutePath);
        if (owner is null)
        {
            return;
        }

        if (!changedByProject.TryGetValue(owner.Name, out var files))
        {
            files = [];
            changedByProject[owner.Name] = files;
        }

        files.Add(file);

        if (!file.IsCSharp)
        {
            return;
        }

        if (!baseSourcesByProject.TryGetValue(owner.Name, out var sources))
        {
            sources = [];
            baseSourcesByProject[owner.Name] = sources;
        }

        sources.Add(new BaseSource(absolutePath, baseContent));
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
        IReadOnlyList<ChangedFile> changedFiles,
        IReadOnlyList<BaseSource> baseSources,
        ChangedSymbolResolver resolver,
        SymbolChangeSet changes,
        CancellationToken cancellationToken)
    {
        var generated = context.GeneratedOutput;

        if (generated.EmittedCount == 0)
        {
            // No generator in this project emits anything. Every SDK project references several
            // that stay silent unless used, so this is the ordinary case and it costs nothing.
            return;
        }

        if (generated.InCompilation.Count == 0)
        {
            changes.AddProjectWide(context.Name, ProjectWideCause.SourceGenerator,
                $"{context.Name} runs source generators whose output is not in the analysed compilation, " +
                "so a change to their input cannot be attributed at symbol granularity");
            return;
        }

        // A generator may read an AdditionalFile, an embedded resource, anything. Only a diff made
        // entirely of C# can be replayed faithfully by substituting syntax trees.
        var comparison = changedFiles.All(f => f.IsCSharp)
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
        ProjectContext context,
        ChangedFile file,
        string? oldContent,
        SyntaxTree tree,
        CancellationToken cancellationToken) =>
        oldContent is not null &&
        file.Kind == FileChangeKind.Modified &&
        context.GeneratedOutput.EmittedCount == 0 &&
        TriviaOnlyChange.Applies(oldContent, tree.GetText(cancellationToken).ToString(), tree.Options, cancellationToken);

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

    private static bool PathsEqual(string a, string b) => string.Equals(a, b, PathComparison);

    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static StringComparer PathComparer =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
