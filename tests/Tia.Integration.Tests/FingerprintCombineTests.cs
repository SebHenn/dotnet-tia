using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// The reuse key. A project's cached fragment stands only while its own content and every
/// declaration surface it binds against are unchanged - so what this folds in, and how far down
/// the reference chain it reaches, is what decides whether a stale fragment can survive a run.
/// </summary>
public sealed class FingerprintCombineTests
{
    // App -> Middle -> Core, plus an unrelated Other.
    private static readonly Dictionary<string, IReadOnlyList<string>> References = new(StringComparer.Ordinal)
    {
        ["App"] = ["Middle"],
        ["Middle"] = ["Core"],
        ["Core"] = [],
        ["Other"] = [],
    };

    private static string Key(
        string project,
        (string Project, string Hash)[] content,
        (string Project, string Hash)[] surface) =>
        ProjectFingerprint.Combine(
            project,
            content.ToDictionary(c => c.Project, c => c.Hash, StringComparer.Ordinal),
            surface.ToDictionary(s => s.Project, s => s.Hash, StringComparer.Ordinal),
            References);

    private static readonly (string, string)[] BaseContent =
        [("App", "a1"), ("Middle", "m1"), ("Core", "c1"), ("Other", "o1")];

    private static readonly (string, string)[] BaseSurface =
        [("App", "as1"), ("Middle", "ms1"), ("Core", "cs1"), ("Other", "os1")];

    [Fact]
    public void The_same_inputs_give_the_same_key()
    {
        Assert.Equal(Key("App", BaseContent, BaseSurface), Key("App", BaseContent, BaseSurface));
    }

    [Fact]
    public void A_projects_own_content_moves_its_key()
    {
        Assert.NotEqual(
            Key("App", BaseContent, BaseSurface),
            Key("App", [("App", "a2"), ("Middle", "m1"), ("Core", "c1"), ("Other", "o1")], BaseSurface));
    }

    [Fact]
    public void A_direct_dependencys_surface_moves_the_key()
    {
        Assert.NotEqual(
            Key("App", BaseContent, BaseSurface),
            Key("App", BaseContent, [("App", "as1"), ("Middle", "ms2"), ("Core", "cs1"), ("Other", "os1")]));
    }

    [Fact]
    public void A_transitive_dependencys_surface_moves_the_key()
    {
        // App does not reference Core directly, but Middle exposes Core's types in its own
        // signatures, so App binds against them. A key that stopped at direct references would
        // leave App reusing a fragment built against declarations that no longer exist.
        Assert.NotEqual(
            Key("App", BaseContent, BaseSurface),
            Key("App", BaseContent, [("App", "as1"), ("Middle", "ms1"), ("Core", "cs2"), ("Other", "os1")]));
    }

    [Fact]
    public void A_dependencys_content_alone_does_not_move_the_key()
    {
        // The whole reason the surface is hashed separately: a body-only edit in Core cannot
        // change how App binds, and folding Core's content in is what made the cache useless.
        Assert.Equal(
            Key("App", BaseContent, BaseSurface),
            Key("App", [("App", "a1"), ("Middle", "m1"), ("Core", "c2"), ("Other", "o1")], BaseSurface));
    }

    [Fact]
    public void An_unrelated_projects_surface_does_not_move_the_key()
    {
        Assert.Equal(
            Key("App", BaseContent, BaseSurface),
            Key("App", BaseContent, [("App", "as1"), ("Middle", "ms1"), ("Core", "cs1"), ("Other", "os2")]));
    }

    [Fact]
    public void A_projects_own_surface_does_not_move_its_own_key()
    {
        // Its own surface is derived from its own content, which is already folded in. Counting it
        // twice would be harmless; counting it instead of the content would not be.
        Assert.Equal(
            Key("App", BaseContent, BaseSurface),
            Key("App", BaseContent, [("App", "as2"), ("Middle", "ms1"), ("Core", "cs1"), ("Other", "os1")]));
    }

    [Fact]
    public void Two_projects_with_the_same_inputs_still_get_different_keys()
    {
        Assert.NotEqual(Key("Core", BaseContent, BaseSurface), Key("Other", BaseContent, BaseSurface));
    }

    [Fact]
    public void A_reference_cycle_terminates()
    {
        // Project references cannot legally cycle, but a malformed or generated solution graph can
        // present one, and hanging is a worse failure than any answer.
        var cyclic = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["A"] = ["B"],
            ["B"] = ["A"],
        };

        var content = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "a", ["B"] = "b" };
        var surface = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "as", ["B"] = "bs" };

        Assert.NotEmpty(ProjectFingerprint.Combine("A", content, surface, cyclic));
    }

    [Fact]
    public void A_missing_dependency_is_not_silently_equal_to_a_present_one()
    {
        // An unresolvable reference records a placeholder rather than nothing, so a project that
        // failed to load does not produce the same key as one whose surface happened to be empty.
        var present = ProjectFingerprint.Combine(
            "App",
            BaseContent.ToDictionary(c => c.Item1, c => c.Item2, StringComparer.Ordinal),
            BaseSurface.ToDictionary(s => s.Item1, s => s.Item2, StringComparer.Ordinal),
            References);

        var missing = ProjectFingerprint.Combine(
            "App",
            BaseContent.ToDictionary(c => c.Item1, c => c.Item2, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["App"] = "as1", ["Middle"] = "ms1" },
            References);

        Assert.NotEqual(present, missing);
    }
}
