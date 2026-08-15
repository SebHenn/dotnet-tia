using Tia.Core.Analysis;
using Tia.Core.Model;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// Projects in a language this engine does not analyse. They used to be skipped and forgotten,
/// which is worse than it sounds: a changed <c>.fs</c> or <c>.vb</c> file found no owning project,
/// so it widened nothing, and a C# test project exercising that library selected <b>zero</b> tests
/// for a change to it. A widening that reaches too far costs a full run; this cost a missed test.
/// </summary>
public sealed class ForeignProjectTests
{
    private static ProjectDescriptor Project(string name, params string[] references) => new()
    {
        Name = name,
        AssemblyName = name,
        FilePath = Path.Combine(Path.GetTempPath(), name, name + ".csproj"),
        ProjectReferences = references,
    };

    /// <summary>
    /// The widening source does not have to be a project the engine loaded. Dependent expansion
    /// works off reference *names*, and a C# project that references a VB one records that name
    /// like any other - so naming the foreign project is enough to reach everything above it.
    /// </summary>
    [Fact]
    public void A_foreign_project_widens_the_csharp_projects_that_reference_it()
    {
        // Tests -> Wrapper -> LegacyVb, with an unrelated Other alongside.
        var descriptors = new[]
        {
            Project("Tests", "Wrapper"),
            Project("Wrapper", "LegacyVb"),
            Project("Other"),
        };

        var widened = SelectionReporter.ExpandToDependents(
            descriptors,
            [new ProjectWideChange("LegacyVb", ProjectWideCause.ForeignLanguage, "a .vb file changed")]);

        Assert.Contains("LegacyVb", widened);
        Assert.Contains("Wrapper", widened);
        Assert.Contains("Tests", widened);
        Assert.DoesNotContain("Other", widened);
    }

    /// <summary>
    /// The cause exists to be reported, not just to widen. A reader who sees a whole test project
    /// selected for a one-line change deserves to be told it was a language boundary rather than
    /// assume the engine is being careless.
    /// </summary>
    [Fact]
    public void The_cause_is_distinct_from_an_ordinary_content_file()
    {
        Assert.NotEqual(ProjectWideCause.ContentFile, ProjectWideCause.ForeignLanguage);
    }

    /// <summary>
    /// A widening that does not widen would be a silent miss wearing a report entry. Pinned
    /// because <c>WidensProject: false</c> is a legitimate value for other causes - a generator
    /// whose output was seeded precisely - and this one must never be given it.
    /// </summary>
    [Fact]
    public void A_foreign_language_change_always_widens()
    {
        var change = new ProjectWideChange("LegacyVb", ProjectWideCause.ForeignLanguage, "a .vb file changed");

        Assert.True(change.WidensProject);
        Assert.Contains("LegacyVb", SelectionReporter.ExpandToDependents([Project("LegacyVb")], [change]));
    }
}
