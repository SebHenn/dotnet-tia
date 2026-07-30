namespace Tia.Core.Analysis;

public enum ProjectWideCause
{
    /// <summary>A <c>const</c> field or enum member changed. Callers inline the value at compile
    /// time and carry no reference to the symbol, so no call-graph walk can find them.</summary>
    ConstantInlining,

    /// <summary>A global using or file-scoped directive changed, altering name binding project-wide.</summary>
    GlobalUsing,

    /// <summary>A non-source file changed - test data, resources, embedded content.</summary>
    ContentFile,

    /// <summary>The project runs source generators: generated trees have no file on disk, so any
    /// change to generator input has to be treated as project-wide.</summary>
    SourceGenerator,

    /// <summary>Reflection was found in a changed file or in the impact set.</summary>
    Reflection,

    /// <summary>A type that existed in the base revision is gone, so its former shape cannot be
    /// mapped onto anything in the current compilation.</summary>
    DeletedType,
}

/// <param name="WidensProject">
/// False for a change that is worth reporting but that has already been handled precisely - a
/// generator whose output was seeded into the changed set, for instance. Those still belong in the
/// report, because the reader needs to know the generator was involved, but they must not put the
/// project into full scope on top of it.
/// </param>
public sealed record ProjectWideChange(string ProjectName, ProjectWideCause Cause, string Detail, bool WidensProject = true);

/// <summary>The result of mapping a diff onto symbols: seed nodes plus anything that has to be
/// widened to whole-project scope because symbol granularity cannot express it.</summary>
public sealed class SymbolChangeSet
{
    public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

    public List<ProjectWideChange> ProjectWide { get; } = [];

    public List<string> UnmappedChanges { get; } = [];

    /// <summary>Things worth telling the user about how the diff was read, but which change
    /// nothing about what is selected.</summary>
    public List<string> Notes { get; } = [];

    public bool IsEmpty => Keys.Count == 0 && ProjectWide.Count == 0;

    public void Add(string key) => Keys.Add(key);

    public void AddProjectWide(string projectName, ProjectWideCause cause, string detail, bool widensProject = true)
    {
        foreach (var existing in ProjectWide)
        {
            if (existing.ProjectName == projectName && existing.Cause == cause && existing.Detail == detail)
            {
                return;
            }
        }

        ProjectWide.Add(new ProjectWideChange(projectName, cause, detail, widensProject));
    }

    public void Merge(SymbolChangeSet other)
    {
        Keys.UnionWith(other.Keys);
        foreach (var change in other.ProjectWide)
        {
            AddProjectWide(change.ProjectName, change.Cause, change.Detail, change.WidensProject);
        }

        UnmappedChanges.AddRange(other.UnmappedChanges);
        Notes.AddRange(other.Notes);
    }
}
