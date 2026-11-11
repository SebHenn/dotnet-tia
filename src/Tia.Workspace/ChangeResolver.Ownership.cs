using Tia.Core.Analysis;
using Tia.Core.Caching;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Safety;

namespace Tia.Workspace;

// Which project a changed path belongs to, and what a path no loaded project owns widens.
internal sealed partial class ChangeResolver
{
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
}
