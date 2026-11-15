using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Safety;

namespace Tia.Workspace;

// Reqnroll / SpecFlow feature files: a changed one selects the class generated from it (#172).
internal sealed partial class ChangeResolver
{
    /// <summary>
    /// Whether any source in the solution reads <c>.feature</c> files itself, read once and only
    /// when a feature file is in the diff. The generated classes are left out: each names its own
    /// feature in a <c>#line</c> directive.
    /// </summary>
    private sealed class FeatureGuard
    {
        private readonly Lazy<bool> _readsFeatures;

        public FeatureGuard(LoadedWorkspace workspace, CancellationToken cancellationToken) =>
            _readsFeatures = new Lazy<bool>(() =>
            {
                foreach (var context in workspace.Projects)
                {
                    foreach (var document in context.Project.Documents)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (document.FilePath?.EndsWith(".feature.cs", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            continue;
                        }

                        if (FeatureFiles.ReadsFeatureFiles(document.GetTextAsync(cancellationToken).GetAwaiter().GetResult().ToString()))
                        {
                            return true;
                        }
                    }
                }

                return false;
            });

        public bool SomethingReadsFeatureFiles => _readsFeatures.Value;
    }

    /// <summary>
    /// The project file, or a <c>Directory.Build.props</c>/<c>.targets</c> above it, says anything
    /// about features: it may ship the files to a test that reads them. Text matched; widens.
    /// </summary>
    private static bool BuildFilesMentionFeatures(string projectFile)
    {
        for (var directory = Path.GetDirectoryName(projectFile); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate) && File.ReadAllText(candidate).Contains("feature", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return File.ReadAllText(projectFile).Contains("feature", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Seeds the methods of the class generated from a changed <c>.feature</c> file in place of
    /// widening the project that holds it. False leaves the file to <see cref="WidenContent"/>.
    /// </summary>
    /// <remarks>
    /// Why this cannot cause a miss: it narrows only when the file's generated class is in the
    /// compilation, <see cref="FeatureFiles.Matches"/> says the class still has a method for every
    /// scenario the feature names (so the class selection contains whatever the edit changes, and a
    /// scenario added since the last build widens), nothing else in the solution reads feature
    /// files, no other project links the file in, and the project file says nothing about features.
    /// A step definition the feature calls is C#, which the graph traces on its own.
    /// </remarks>
    private static bool TryNarrowFeature(
        LoadedWorkspace workspace,
        LinkedItems linked,
        ImpactGraph graph,
        FeatureGuard guard,
        ChangedFile file,
        string absolutePath,
        SymbolChangeSet changes,
        Dictionary<string, bool> changedProjects,
        CancellationToken cancellationToken)
    {
        if (!file.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase) ||
            file.OldPath is not null ||
            !file.ExistsOnNewSide ||
            !File.Exists(absolutePath) ||
            FindOwningProject(workspace, absolutePath) is not { } owner ||
            linked.InWorkspace(absolutePath).Any() ||
            guard.SomethingReadsFeatureFiles)
        {
            return false;
        }

        var context = workspace.Projects.Single(c => c.Descriptor.Name == owner.Name);
        if (context.Project.FilePath is not { } projectFile || BuildFilesMentionFeatures(projectFile))
        {
            return false;
        }

        var generatedPath = absolutePath + ".cs";
        var document = context.Project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, generatedPath, PathComparison));
        if (document is null ||
            !FeatureFiles.Matches(File.ReadAllText(absolutePath), document.GetTextAsync(cancellationToken).GetAwaiter().GetResult().ToString()) ||
            document.GetSemanticModelAsync(cancellationToken).GetAwaiter().GetResult() is not { } model ||
            document.GetSyntaxRootAsync(cancellationToken).GetAwaiter().GetResult() is not { } root)
        {
            return false;
        }

        var tests = new List<string>();
        foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Parent is not TypeDeclarationSyntax))
        {
            if (SymbolKeys.For(model.GetDeclaredSymbol(declaration, cancellationToken)) is not { } typeKey)
            {
                return false;
            }

            tests.AddRange(graph.MembersOfType(typeKey).Where(m => graph.TryGetNode(m) is { Kind: SymbolNodeKind.Method }));
        }

        if (tests.Count == 0)
        {
            return false;
        }

        foreach (var test in tests)
        {
            changes.Add(test);
        }

        changedProjects[owner.Name] = false;
        changes.AddProjectWide(
            owner.Name,
            ProjectWideCause.ContentFile,
            $"{file.Path} is a feature file whose generated class still has a method for each of its scenarios; " +
            "nothing in the solution reads feature files, so only that class is impacted",
            widensProject: false);
        return true;
    }
}
