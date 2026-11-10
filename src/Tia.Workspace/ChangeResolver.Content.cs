using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Safety;

namespace Tia.Workspace;

// Changes that are not C# source: content files and package version bumps.
internal sealed partial class ChangeResolver
{
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
}
