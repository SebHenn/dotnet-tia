using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis;
using Tia.Core.Model;
using Tia.Core.Reporting;

namespace Tia.Workspace;

/// <param name="How">What the project does with the file, for the widening's detail.</param>
internal sealed record LinkingProject(ProjectDescriptor Project, string How);

/// <summary>
/// The projects that include a file from outside their own folder (#123).
/// </summary>
/// <remarks>
/// <para>
/// Ownership by folder is the cheap answer and usually the right one, but a project file can name
/// anything: <c>&lt;EmbeddedResource Include="..\..\data\tzdb.nzd" /&gt;</c>, test data copied in
/// from a shared folder, a generator's schema in <c>AdditionalFiles</c>, a vendored DLL by
/// <c>HintPath</c>. Such a file has no folder owner, so a change to it used to select nothing at
/// all - the NodaTime <c>Tzdb.nzd</c> miss again, reached through a link instead of an extension.
/// </para>
/// <para>
/// Two sources, cheapest first. The workspace already holds every additional file, analyzer
/// config and referenced assembly, so those cost a lookup. Embedded resources and files copied to
/// the output are invisible to it, and finding them means evaluating the project through MSBuild
/// again - a quarter of a second each - so that is spent only for a file no other rule owns, only
/// once per analysis, and only on projects whose build files could link anything at all.
/// </para>
/// </remarks>
internal sealed class LinkedItems(LoadedWorkspace workspace, string repositoryRoot, PhaseClock? clock)
{
    private Dictionary<string, List<LinkingProject>>? _references;

    private EvaluatedLinks? _evaluated;

    private readonly Dictionary<string, CompileItems?> _compileItems = new(PathComparer);

    /// <summary>
    /// Projects the workspace shows including <paramref name="absolutePath"/>: as a document, an
    /// additional file, analyzer configuration or a referenced assembly. Costs a lookup.
    /// </summary>
    public IReadOnlyList<LinkingProject> InWorkspace(string absolutePath)
    {
        var found = new List<LinkingProject>();
        var solution = workspace.Solution;

        foreach (var id in solution.GetDocumentIdsWithFilePath(absolutePath))
        {
            if (Context(id.ProjectId) is not { } context)
            {
                continue;
            }

            var how = solution.GetDocument(id) is not null ? "compiles it"
                : solution.GetAdditionalDocument(id) is not null ? "reads it as an additional file, which generators and analyzers see"
                : "reads it as analyzer configuration";
            found.Add(new LinkingProject(context.Descriptor, how));
        }

        _references ??= IndexReferences();
        if (_references.TryGetValue(absolutePath, out var referencing))
        {
            found.AddRange(referencing);
        }

        return found;
    }

    /// <summary>
    /// Projects that embed <paramref name="absolutePath"/> as a resource or copy it to their output,
    /// by evaluating them. The first call pays for the evaluation; the rest are lookups.
    /// </summary>
    /// <remarks>
    /// A file deleted by the diff that a project names literally is still in its evaluated items:
    /// MSBuild keeps a literal include whether or not the file exists. One a glob included is not,
    /// so the globs are matched as well. Their metadata may come from the including element, a
    /// later <c>Update</c> or the item type's definition, and all three are read (#156).
    /// </remarks>
    public IReadOnlyList<LinkingProject> Evaluated(string absolutePath, bool exists)
    {
        _evaluated ??= Evaluate();

        var literal = _evaluated.Files.GetValueOrDefault(absolutePath) ?? [];
        if (exists)
        {
            return literal;
        }

        var globbed = _evaluated.Globs
            .Where(g => g.Glob.IsMatch(absolutePath))
            .Select(g => ReachesTheRun(g.ItemType, g.Copy ?? _evaluated.CopyFor(g.Project.Project.FilePath, g.ItemType, absolutePath)) is { } how
                ? g.Project with { How = how }
                : null)
            .OfType<LinkingProject>();

        return [.. literal.Concat(globbed).DistinctBy(p => (p.Project.Name, p.Project.TargetFramework))];
    }

    /// <summary>
    /// Whether a C# file at <paramref name="absolutePath"/> is in a compilation, as the project
    /// files say. False only when every project that could compile it was evaluated and none does;
    /// null when one could not be evaluated.
    /// </summary>
    /// <remarks>
    /// For the old side of a deleted or renamed file, which is no longer anywhere the workspace can
    /// show (#156). The project files are those of the new side, but they are also the old side's:
    /// a selective analysis only gets here when no build file changed, because a change to one runs
    /// everything. The projects asked are the one whose folder holds the path, and any that
    /// compile a file from outside their own folder.
    /// </remarks>
    public bool? Compiles(string absolutePath, ProjectDescriptor? folderOwner)
    {
        var candidates = workspace.Projects
            .Where(p => p.Descriptor == folderOwner || LinksSource(p))
            .Select(p => p.Descriptor.FilePath)
            .Distinct(PathComparer);

        foreach (var projectFile in candidates)
        {
            if (!_compileItems.TryGetValue(projectFile, out var items))
            {
                items = EvaluateCompile(projectFile);
                _compileItems[projectFile] = items;
            }

            if (items is null)
            {
                return null;
            }

            if (items.Literal.Contains(absolutePath) || items.Globs.Any(g => g.IsMatch(absolutePath)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Projects whose evaluation failed, so whatever they link is unknown.</summary>
    public IReadOnlyList<string> Unevaluated => _evaluated?.Failures ?? [];

    private ProjectContext? Context(ProjectId id) => workspace.Projects.FirstOrDefault(p => p.Project.Id == id);

    private Dictionary<string, List<LinkingProject>> IndexReferences()
    {
        var index = new Dictionary<string, List<LinkingProject>>(PathComparer);
        var root = RootPrefix();

        foreach (var context in workspace.Projects)
        {
            foreach (var reference in context.Project.MetadataReferences.OfType<PortableExecutableReference>())
            {
                // The framework and every package live outside the repository, and are thousands
                // of entries nothing in a diff can name.
                if (reference.FilePath is not { } path || !path.StartsWith(root, PathComparison))
                {
                    continue;
                }

                Add(index, Path.GetFullPath(path), new LinkingProject(context.Descriptor, "references it as an assembly"));
            }
        }

        return index;
    }

    private EvaluatedLinks Evaluate()
    {
        var started = Stopwatch.GetTimestamp();
        var links = new EvaluatedLinks();
        var root = RootPrefix();
        var mentions = new BuildFileScan(repositoryRoot);

        // One evaluation per project file: a multi-targeted project is several contexts that share
        // it, and every one of them ships what the file links.
        foreach (var group in workspace.Projects.GroupBy(p => p.Descriptor.FilePath, PathComparer))
        {
            if (!mentions.CouldLink(group.Key))
            {
                continue;
            }

            using var collection = new ProjectCollection();
            Microsoft.Build.Evaluation.Project project;
            try
            {
                project = collection.LoadProject(group.Key);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Broad for the reason MsBuildPropertyProbe's is: an SDK that will not resolve
                // throws whatever its resolver chose.
                links.Failures.Add(Path.GetFileName(group.Key));
                continue;
            }

            var directory = Path.GetDirectoryName(group.Key)! + Path.DirectorySeparatorChar;

            foreach (var item in project.Items)
            {
                if (ReachesTheRun(item.ItemType, item.GetMetadataValue("CopyToOutputDirectory")) is not { } how)
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(project.DirectoryPath, item.EvaluatedInclude));
                if (!path.StartsWith(root, PathComparison) || path.StartsWith(directory, PathComparison))
                {
                    continue;
                }

                foreach (var context in group)
                {
                    Add(links.Files, path, new LinkingProject(context.Descriptor, how));
                }
            }

            foreach (var glob in project.GetAllGlobs())
            {
                var type = glob.ItemElement.ItemType;
                if (type is not ("EmbeddedResource" or "None" or "Content"))
                {
                    continue;
                }

                // Null when the element does not say, so a deleted file's copy is looked up where
                // else it can be set.
                var copy = glob.ItemElement.Metadata.FirstOrDefault(m => m.Name == "CopyToOutputDirectory")?.Value;
                foreach (var context in group)
                {
                    links.Globs.Add((glob.MsBuildGlob, type, copy, new LinkingProject(context.Descriptor, string.Empty)));
                }
            }

            links.Copies[group.Key] = Copies(project);

            collection.UnloadAllProjects();
        }

        clock?.Record(nameof(PhaseTimings.LinkedItemEvaluationSeconds), started);
        return links;
    }

    /// <summary>
    /// Whether an item of this type ends up where a test can read it, and how, or null.
    /// </summary>
    /// <remarks>
    /// A <c>None</c> item is otherwise nothing at all to the build: the common case is a package's
    /// readme, <c>&lt;None Include="..\..\README.md" Pack="true" /&gt;</c>, and widening a library
    /// for every edit to the repository's readme would be a full run bought for nothing.
    /// </remarks>
    private static string? ReachesTheRun(string itemType, string copyToOutput) => itemType switch
    {
        "EmbeddedResource" => "embeds it as a resource",
        "None" or "Content" when copyToOutput.Length > 0 && !copyToOutput.Equals("Never", StringComparison.OrdinalIgnoreCase)
            => "copies it to its output",
        _ => null,
    };

    private string RootPrefix() => Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static void Add(Dictionary<string, List<LinkingProject>> index, string path, LinkingProject project)
    {
        if (!index.TryGetValue(path, out var list))
        {
            list = [];
            index[path] = list;
        }

        list.Add(project);
    }

    /// <summary>
    /// Where a project sets <c>CopyToOutputDirectory</c> apart from an including element: each
    /// <c>Update</c> that sets it, and the item type's definition.
    /// </summary>
    private static ProjectCopies Copies(Microsoft.Build.Evaluation.Project project)
    {
        var updates = new List<(string ItemType, Microsoft.Build.Globbing.IMSBuildGlob Glob, string Copy)>();

        foreach (var element in project.Xml.AllChildren.OfType<Microsoft.Build.Construction.ProjectItemElement>())
        {
            if (element.Update.Length == 0 ||
                element.Metadata.LastOrDefault(m => m.Name == "CopyToOutputDirectory") is not { } copy)
            {
                continue;
            }

            // Conditions are not weighed: an Update that might apply counts as applying, which can
            // only widen.
            foreach (var pattern in project.ExpandString(element.Update).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                updates.Add((element.ItemType, Microsoft.Build.Globbing.MSBuildGlob.Parse(project.DirectoryPath, pattern), project.ExpandString(copy.Value)));
            }
        }

        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in (string[])["None", "Content"])
        {
            if (project.ItemDefinitions.TryGetValue(type, out var definition) &&
                definition.GetMetadataValue("CopyToOutputDirectory") is { Length: > 0 } value)
            {
                defaults[type] = value;
            }
        }

        return new ProjectCopies(updates, defaults);
    }

    private sealed record ProjectCopies(
        IReadOnlyList<(string ItemType, Microsoft.Build.Globbing.IMSBuildGlob Glob, string Copy)> Updates,
        IReadOnlyDictionary<string, string> Defaults);

    private sealed class EvaluatedLinks
    {
        public Dictionary<string, List<LinkingProject>> Files { get; } = new(PathComparer);

        /// <summary>Every glob that includes a file the run may read; <c>Copy</c> null when its element does not set it.</summary>
        public List<(Microsoft.Build.Globbing.IMSBuildGlob Glob, string ItemType, string? Copy, LinkingProject Project)> Globs { get; } = [];

        public Dictionary<string, ProjectCopies> Copies { get; } = new(PathComparer);

        public List<string> Failures { get; } = [];

        /// <summary>
        /// The copy a deleted file of <paramref name="itemType"/> got when its including element
        /// did not set one: the last <c>Update</c> that matches it, else the type's definition.
        /// </summary>
        public string CopyFor(string projectFile, string itemType, string absolutePath)
        {
            if (!Copies.TryGetValue(projectFile, out var copies))
            {
                return string.Empty;
            }

            var update = copies.Updates.LastOrDefault(u =>
                string.Equals(u.ItemType, itemType, StringComparison.OrdinalIgnoreCase) && u.Glob.IsMatch(absolutePath));

            return update.Copy ?? copies.Defaults.GetValueOrDefault(itemType) ?? string.Empty;
        }
    }

    /// <summary>A project's <c>Compile</c> items: the literal paths, and the globs net of their excludes and removes.</summary>
    private sealed record CompileItems(HashSet<string> Literal, IReadOnlyList<Microsoft.Build.Globbing.IMSBuildGlob> Globs);

    private static CompileItems? EvaluateCompile(string projectFile)
    {
        using var collection = new ProjectCollection();
        try
        {
            var project = collection.LoadProject(projectFile);
            var literal = new HashSet<string>(
                project.GetItems("Compile").Select(i => Path.GetFullPath(Path.Combine(project.DirectoryPath, i.EvaluatedInclude))),
                PathComparer);
            var globs = project.GetAllGlobs("Compile").Select(g => g.MsBuildGlob).ToList();
            return new CompileItems(literal, globs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // As in Evaluate: whatever an SDK resolver chose to throw. Unknown, so not "no".
            return null;
        }
        finally
        {
            collection.UnloadAllProjects();
        }
    }

    /// <summary>Whether a project compiles C# from outside its own folder, and so might have compiled any path.</summary>
    private static bool LinksSource(ProjectContext context)
    {
        var directory = context.Descriptor.Directory + Path.DirectorySeparatorChar;
        return context.Project.Documents.Any(d => d.FilePath is { } path &&
                                                  path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                                                  !path.StartsWith(directory, PathComparison) &&
                                                  !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, PathComparison));
    }

    /// <summary>
    /// Whether a project's build files could link a file the run reads, judged from their text.
    /// </summary>
    /// <remarks>
    /// Evaluation costs a quarter of a second a project, and the diff that reaches it is usually a
    /// readme or a workflow file that nothing links. A project whose own file, the
    /// <c>Directory.Build</c> files above it and whatever those import name neither an embedded
    /// resource nor a copy to output cannot link one, because the SDK's own items never leave the
    /// project's folder. An import this cannot resolve from its text counts as naming both, so the
    /// shortcut is only ever taken when it is certain.
    /// </remarks>
    private sealed class BuildFileScan(string repositoryRoot)
    {
        private static readonly string[] Markers = ["EmbeddedResource", "CopyToOutputDirectory"];

        private readonly Dictionary<string, bool> _files = new(PathComparer);

        public bool CouldLink(string projectFile)
        {
            if (Mentions(projectFile))
            {
                return true;
            }

            var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile));

            while (directory is not null)
            {
                foreach (var name in (string[])["Directory.Build.props", "Directory.Build.targets"])
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate) && Mentions(candidate))
                    {
                        return true;
                    }
                }

                if (string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), root, PathComparison))
                {
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return false;
        }

        private bool Mentions(string file)
        {
            if (_files.TryGetValue(file, out var known))
            {
                return known;
            }

            // Assumed while it is being read, so an import cycle ends instead of recursing forever.
            _files[file] = false;
            return _files[file] = Read(file);
        }

        private bool Read(string file)
        {
            string text;
            XDocument document;
            try
            {
                text = File.ReadAllText(file);
                document = XDocument.Parse(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return true;
            }

            if (Markers.Any(m => text.Contains(m, StringComparison.Ordinal)))
            {
                return true;
            }

            foreach (var import in document.Descendants().Where(e => e.Name.LocalName == "Import"))
            {
                if (Resolve(import.Attribute("Project")?.Value, file) is not { } imported)
                {
                    return true;
                }

                if (File.Exists(imported) && Mentions(imported))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The file an <c>Import</c> names, or null when its path is computed: anything but the two
        /// directory properties a build file uses to find its neighbours.
        /// </summary>
        private static string? Resolve(string? project, string importingFile)
        {
            if (string.IsNullOrWhiteSpace(project))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(importingFile)! + Path.DirectorySeparatorChar;
            var path = project
                .Replace("$(MSBuildThisFileDirectory)", directory, StringComparison.OrdinalIgnoreCase);

            if (path.Contains("$(", StringComparison.Ordinal) || path.Contains('*', StringComparison.Ordinal))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(importingFile)!, path));
        }
    }

    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static StringComparer PathComparer =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
