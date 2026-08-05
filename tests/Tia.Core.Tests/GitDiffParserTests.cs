using Tia.Core.Diff;

namespace Tia.Core.Tests;

public sealed class GitDiffParserTests
{
    [Fact]
    public void NameStatus_reads_modifications_and_additions()
    {
        var output = "M\0src/Foo.cs\0A\0src/Bar.cs\0D\0src/Gone.cs\0";

        var files = GitDiffParser.ParseNameStatus(output);

        Assert.Collection(files,
            f => Assert.Equal((FileChangeKind.Modified, "src/Foo.cs"), (f.Kind, f.Path)),
            f => Assert.Equal((FileChangeKind.Added, "src/Bar.cs"), (f.Kind, f.Path)),
            f => Assert.Equal((FileChangeKind.Deleted, "src/Gone.cs"), (f.Kind, f.Path)));
    }

    [Fact]
    public void NameStatus_reads_the_two_paths_of_a_rename()
    {
        var output = "R096\0src/Old.cs\0src/New.cs\0M\0src/Other.cs\0";

        var files = GitDiffParser.ParseNameStatus(output);

        var rename = Assert.Single(files, f => f.Kind == FileChangeKind.Renamed);
        Assert.Equal("src/Old.cs", rename.OldPath);
        Assert.Equal("src/New.cs", rename.Path);
        Assert.Equal("src/Old.cs", rename.OldSidePath);
        Assert.Contains(files, f => f.Path == "src/Other.cs");
    }

    [Fact]
    public void NameStatus_survives_paths_containing_spaces()
    {
        var files = GitDiffParser.ParseNameStatus("M\0src/My Folder/A B.cs\0");

        Assert.Equal("src/My Folder/A B.cs", Assert.Single(files).Path);
    }

    [Fact]
    public void Hunks_reads_both_sides()
    {
        var diff = """
            diff --git a/src/Foo.cs b/src/Foo.cs
            index 1234567..89abcde 100644
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -10,3 +10,4 @@
            -old
            +new
            @@ -40 +41,2 @@
            +added
            """;

        var (old, @new) = GitDiffParser.ParseHunks(diff);

        Assert.Equal([new LineRange(10, 12), new LineRange(40, 40)], old);
        Assert.Equal([new LineRange(10, 13), new LineRange(41, 42)], @new);
    }

    [Fact]
    public void Hunks_turns_a_pure_insertion_into_a_range_on_the_other_side()
    {
        // `@@ -7,0 +8,2 @@` is an insertion: nothing was removed, so the old side has a zero-length
        // hunk that still has to name the region the insertion touches.
        var (old, @new) = GitDiffParser.ParseHunks("@@ -7,0 +8,2 @@\n");

        Assert.Equal(new LineRange(7, 8), Assert.Single(old));
        Assert.Equal(new LineRange(8, 9), Assert.Single(@new));
    }

    [Fact]
    public void Hunks_ignores_non_header_lines()
    {
        var (old, @new) = GitDiffParser.ParseHunks("not a hunk\n+@@ fake @@\n");

        Assert.Empty(old);
        Assert.Empty(@new);
    }
}
