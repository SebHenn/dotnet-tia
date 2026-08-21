using Tia.Core.Diff;

namespace Tia.Core.Tests;

/// <summary>
/// Splitting one multi-file diff into per-file line ranges.
/// </summary>
/// <remarks>
/// <para>
/// <c>ParseHunks</c> collects every hunk it sees and ignores the file headers, which is right for a
/// one-file diff and silently wrong for any other - so <c>DiffResolver</c> ran one <c>git diff</c>
/// per changed file. On a ten-file change that was ten process spawns and most of the measured diff
/// cost.
/// </para>
/// <para>
/// Attribution is a correctness surface, not a performance one: ranges landing on the wrong file
/// produce the wrong changed symbols, which is a missed test. Hence the leak case below, and hence
/// the caller treating an absent file as changed end to end rather than as changed nowhere.
/// </para>
/// </remarks>
public sealed class HunkAttributionTests
{
    /// <summary>The whole point: two files in one diff keep their own ranges.</summary>
    [Fact]
    public void Each_file_gets_only_its_own_hunks()
    {
        var byFile = GitDiffParser.ParseHunksByFile(
            """
            diff --git a/src/Alpha.cs b/src/Alpha.cs
            index 111..222 100644
            --- a/src/Alpha.cs
            +++ b/src/Alpha.cs
            @@ -10 +10 @@
            diff --git a/src/Beta.cs b/src/Beta.cs
            index 333..444 100644
            --- a/src/Beta.cs
            +++ b/src/Beta.cs
            @@ -50,2 +50,3 @@
            """);

        Assert.Equal(10, Assert.Single(byFile["src/Alpha.cs"].New).Start);
        Assert.Equal(50, Assert.Single(byFile["src/Beta.cs"].New).Start);

        // The failure this replaces: every hunk on every file.
        Assert.Single(byFile["src/Alpha.cs"].New);
        Assert.Single(byFile["src/Beta.cs"].New);
    }

    /// <summary>
    /// A rename under <c>-M</c> is one block naming two paths, and the caller may look up either -
    /// the old side to find what was removed, the new side to find what is there now.
    /// </summary>
    [Fact]
    public void A_rename_is_reachable_by_either_path()
    {
        var byFile = GitDiffParser.ParseHunksByFile(
            """
            diff --git a/src/Old.cs b/src/New.cs
            similarity index 90%
            rename from src/Old.cs
            rename to src/New.cs
            --- a/src/Old.cs
            +++ b/src/New.cs
            @@ -3 +3 @@
            """);

        Assert.True(byFile.ContainsKey("src/Old.cs"));
        Assert.True(byFile.ContainsKey("src/New.cs"));
        Assert.Equal(3, Assert.Single(byFile["src/New.cs"].New).Start);
        Assert.Equal(3, Assert.Single(byFile["src/Old.cs"].Old).Start);
    }

    /// <summary>
    /// The soundness case. A block whose headers this does not recognise must not pour its hunks
    /// into whichever file happened to come before it.
    /// </summary>
    [Fact]
    public void An_unrecognised_block_does_not_leak_into_the_previous_file()
    {
        var byFile = GitDiffParser.ParseHunksByFile(
            """
            diff --git a/src/Alpha.cs b/src/Alpha.cs
            --- a/src/Alpha.cs
            +++ b/src/Alpha.cs
            @@ -10 +10 @@
            diff --git a/assets/logo.png b/assets/logo.png
            Binary files differ
            @@ -999 +999 @@
            """);

        Assert.Equal(10, Assert.Single(byFile["src/Alpha.cs"].New).Start);
        Assert.DoesNotContain(byFile["src/Alpha.cs"].New, r => r.Start == 999);
    }

    /// <summary>
    /// An add has no old side and a delete has no new one, and <c>/dev/null</c> must not become a
    /// key - a diff of twenty added files would otherwise pile all their hunks onto one entry.
    /// </summary>
    /// <remarks>
    /// The old side still carries a range here, and that is deliberate and pre-existing:
    /// <c>TryParseRange</c> reads a zero-count hunk as an insertion point and reports the following
    /// line, so <c>-0,0</c> becomes line 1. <see cref="GitDiffParser.ParseHunks"/> has always done
    /// the same. It is inert for an added file, which has no old tree to resolve anything against.
    /// </remarks>
    [Fact]
    public void Dev_null_is_not_a_path()
    {
        var byFile = GitDiffParser.ParseHunksByFile(
            """
            diff --git a/src/Added.cs b/src/Added.cs
            new file mode 100644
            --- /dev/null
            +++ b/src/Added.cs
            @@ -0,0 +1,20 @@
            """);

        Assert.DoesNotContain("/dev/null", byFile.Keys);
        Assert.Single(byFile);
        Assert.Equal(1, Assert.Single(byFile["src/Added.cs"].New).Start);
        Assert.Equal(20, Assert.Single(byFile["src/Added.cs"].New).End);
    }

    /// <summary>
    /// Git puts nothing but the path on these lines, so everything after the <c>a/</c> or <c>b/</c>
    /// prefix is the path - spaces included. The caller passes `core.quotePath=false` so a
    /// non-ASCII path arrives as itself rather than C-escaped.
    /// </summary>
    [Fact]
    public void A_path_with_spaces_survives()
    {
        var byFile = GitDiffParser.ParseHunksByFile(
            """
            diff --git a/src/My Folder/Wörld.cs b/src/My Folder/Wörld.cs
            --- a/src/My Folder/Wörld.cs
            +++ b/src/My Folder/Wörld.cs
            @@ -7 +7 @@
            """);

        Assert.Equal(7, Assert.Single(byFile["src/My Folder/Wörld.cs"].New).Start);
    }

    [Fact]
    public void An_empty_diff_yields_nothing() =>
        Assert.Empty(GitDiffParser.ParseHunksByFile(string.Empty));

    /// <summary>
    /// The single-file parser still has callers and still has to behave, so the two do not disagree
    /// on the one case where both apply.
    /// </summary>
    [Fact]
    public void It_agrees_with_the_single_file_parser_on_one_file()
    {
        const string Diff =
            """
            diff --git a/src/Alpha.cs b/src/Alpha.cs
            --- a/src/Alpha.cs
            +++ b/src/Alpha.cs
            @@ -10,2 +12,3 @@
            """;

        var (oldRanges, newRanges) = GitDiffParser.ParseHunks(Diff);
        var attributed = GitDiffParser.ParseHunksByFile(Diff)["src/Alpha.cs"];

        Assert.Equal(oldRanges.Select(r => r.Start), attributed.Old.Select(r => r.Start));
        Assert.Equal(newRanges.Select(r => r.Start), attributed.New.Select(r => r.Start));
    }
}
