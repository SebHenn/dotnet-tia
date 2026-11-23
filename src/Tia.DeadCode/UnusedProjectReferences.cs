using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Tia.DeadCode;

/// <summary>One compilation of the solution, as the reference check needs to see it.</summary>
/// <param name="Name">The project name; the same for every target framework of a multi-targeted project.</param>
/// <param name="DirectReferences">Names of the projects this one lists as <c>ProjectReference</c>.</param>
public sealed record ReferenceProject(string Name, Compilation Compilation, IReadOnlyList<string> DirectReferences);

/// <summary>A <c>ProjectReference</c> that nothing in the project source needs.</summary>
public sealed record UnusedReference(string Project, string Reference);

/// <summary>
/// Finds <c>ProjectReference</c>s a project lists and does not use.
/// </summary>
/// <remarks>
/// <para>
/// What it can see is the source: a reference kept so the other assembly is built first, loaded by
/// name at run time, or copied to the output is reported too. That is why the answer is "nothing in
/// the source needs it", not "it can be removed".
/// </para>
/// <para>
/// A reference is reported only when all of these hold, each of which only drops a report:
/// Roslyn names it unused in every framework copy of the project; deleting it from the compilation
/// adds no error, which catches whatever <c>GetUsedAssemblyReferences</c> does not see; and no other
/// project depends on it arriving through this one - a project that uses B without listing B itself
/// gets B only because it references this project, and removing the reference would break it.
/// </para>
/// </remarks>
public static partial class UnusedProjectReferences
{
    public static IReadOnlyList<UnusedReference> Find(IReadOnlyList<ReferenceProject> projects, CancellationToken cancellationToken = default)
    {
        var byCompilation = new Dictionary<Compilation, ReferenceProject>(ReferenceEqualityComparer.Instance);
        foreach (var project in projects)
        {
            byCompilation[project.Compilation] = project;
        }

        var usedBy = new Dictionary<ReferenceProject, HashSet<MetadataReference>>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            usedBy[project] = [.. project.Compilation.GetUsedAssemblyReferences(cancellationToken)];
        }

        var verdicts = new Dictionary<(string Project, string Reference), bool>();
        foreach (var project in projects)
        {
            foreach (var reference in project.Compilation.References.OfType<CompilationReference>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byCompilation.TryGetValue(reference.Compilation, out var target) ||
                    !project.DirectReferences.Contains(target.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                var key = (project.Name, target.Name);
                var needed = usedBy[project].Contains(reference) ||
                    ReachesOthersThrough(project, target, projects, usedBy) ||
                    BreaksWithout(project, reference, cancellationToken);
                verdicts[key] = verdicts.GetValueOrDefault(key) || needed;
            }
        }

        return [.. verdicts
            .Where(pair => !pair.Value)
            .Select(pair => new UnusedReference(pair.Key.Project, pair.Key.Reference))
            .OrderBy(unused => unused.Project, StringComparer.Ordinal)
            .ThenBy(unused => unused.Reference, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether some other project uses <paramref name="target"/> without listing it, so that it
    /// reaches the target only through <paramref name="project"/>.
    /// </summary>
    private static bool ReachesOthersThrough(
        ReferenceProject project,
        ReferenceProject target,
        IReadOnlyList<ReferenceProject> projects,
        Dictionary<ReferenceProject, HashSet<MetadataReference>> usedBy)
    {
        foreach (var other in projects)
        {
            if (ReferenceEquals(other, project) || other.DirectReferences.Contains(target.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var references = other.Compilation.References.OfType<CompilationReference>().ToList();
            if (references.Any(held => ReferenceEquals(held.Compilation, project.Compilation)) &&
                references.FirstOrDefault(held => ReferenceEquals(held.Compilation, target.Compilation)) is { } viaTarget &&
                usedBy[other].Contains(viaTarget))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BreaksWithout(ReferenceProject project, CompilationReference reference, CancellationToken cancellationToken)
    {
        var before = ErrorCount(project.Compilation, cancellationToken);
        return ErrorCount(project.Compilation.RemoveReferences(reference), cancellationToken) > before;
    }

    private static int ErrorCount(Compilation compilation, CancellationToken cancellationToken) =>
        compilation.GetDiagnostics(cancellationToken).Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// Whether the project file names the referenced project anywhere besides the
    /// <c>ProjectReference</c> that lists it - <c>TrimmerRootAssembly</c>, a copy or a
    /// <c>None</c> item, a condition. Such a reference is kept for a reason the source does not
    /// show, so the command does not report it. Reading more of the file as a mention only drops a
    /// report.
    /// </summary>
    public static bool NamedElsewhere(string projectFileText, string referencedProjectFile)
    {
        var name = Path.GetFileNameWithoutExtension(referencedProjectFile);
        var rest = ProjectReferenceElement().Replace(
            projectFileText,
            match => match.Value.Contains(name, StringComparison.OrdinalIgnoreCase) ? string.Empty : match.Value);
        return rest.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the project file has a <c>ProjectReference</c> element that names the project. The
    /// workspace also reports the references a project inherits through the ones it lists, and
    /// those are not the project's to remove; a reference that arrives from an imported props file
    /// is not seen either, which only drops a report.
    /// </summary>
    public static bool Lists(string projectFileText, string referencedProjectFile)
    {
        var name = Path.GetFileNameWithoutExtension(referencedProjectFile);
        return ProjectReferenceElement().Matches(projectFileText)
            .Any(match => match.Value.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"<ProjectReference\b[^>]*?(/>|>.*?</ProjectReference>)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectReferenceElement();
}
