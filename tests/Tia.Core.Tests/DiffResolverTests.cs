using Tia.Core.Diff;

namespace Tia.Core.Tests;

public sealed class DiffResolverTests
{
    [Fact]
    public void Derived_output_is_never_a_change()
    {
        // Without this the tool's own graph cache is a diff entry: the first run writes
        // .tia/graph-*.bin and the second reports it as an addition. obj/*.props is worse - it
        // matches a full-run trigger, so every analysis would bail out.
        var git = new FakeGitClient
        {
            Untracked = [".tia/graph-abc.bin", "src/obj/Debug/App.csproj.nuget.g.props", "src/App/New.cs"],
        };

        var diff = new DiffResolver(git).Resolve("main").Diff!;

        Assert.Equal(["src/App/New.cs"], diff.Files.Select(f => f.Path));
    }

    [Fact]
    public void A_trailing_newline_does_not_become_a_phantom_path()
    {
        // Process output capture appends a newline that NUL-separated git output never contains.
        var git = new FakeGitClient { NameStatusOutput = "M\0src/App/Widget.cs\0\n" };

        var diff = new DiffResolver(git).Resolve("main").Diff!;

        Assert.Equal(["src/App/Widget.cs"], diff.Files.Select(f => f.Path));
    }

    [Fact]
    public void An_untracked_file_is_a_change_even_though_git_diff_never_reports_it()
    {
        var git = new FakeGitClient { Untracked = ["tests/App.Tests/NewTests.cs"] };

        var file = Assert.Single(new DiffResolver(git).Resolve("main").Diff!.Files);

        Assert.Equal(FileChangeKind.Added, file.Kind);

        // No hunks exist for a file git does not track, so it counts as changed end to end
        // rather than as changed nowhere.
        Assert.Equal(1, Assert.Single(file.NewLines).Start);
        Assert.Equal(int.MaxValue, file.NewLines[0].End);
    }

    [Fact]
    public void An_unreachable_base_forces_a_full_run_and_says_the_clone_is_shallow()
    {
        var git = new FakeGitClient { Shallow = true, ResolvableCommits = [] };

        var resolution = new DiffResolver(git).Resolve("origin/main");

        Assert.Null(resolution.Diff);
        Assert.Contains("shallow", resolution.FullRunReason);
    }

    [Fact]
    public void A_diverged_base_is_replaced_by_the_merge_base()
    {
        var git = new FakeGitClient { AncestorResult = false, MergeBaseResult = "merge-base-commit" };

        var diff = new DiffResolver(git).Resolve("origin/main").Diff!;

        Assert.Equal("merge-base-commit", diff.BaseCommit);
    }

    [Fact]
    public void No_merge_base_forces_a_full_run()
    {
        var git = new FakeGitClient { AncestorResult = false, MergeBaseResult = null };

        var resolution = new DiffResolver(git).Resolve("origin/main");

        Assert.Null(resolution.Diff);
        Assert.Contains("merge base", resolution.FullRunReason);
    }

    private sealed class FakeGitClient : IGitClient
    {
        public string RepositoryRoot => "/repo";

        public bool Shallow { get; init; }

        public bool IsShallow => Shallow;

        public string NameStatusOutput { get; init; } = string.Empty;

        public string HunkOutput { get; init; } = string.Empty;

        public IReadOnlyList<string> Untracked { get; init; } = [];

        public bool AncestorResult { get; init; } = true;

        public string? MergeBaseResult { get; init; } = "base-commit";

        public IReadOnlyList<string>? ResolvableCommits { get; init; }

        public string? ResolveCommit(string revision) =>
            ResolvableCommits is null ? "base-commit" : ResolvableCommits.Contains(revision) ? revision : null;

        public string? MergeBase(string a, string b) => MergeBaseResult;

        public bool IsAncestor(string ancestor, string descendant) => AncestorResult;

        public string NameStatus(string baseCommit) => NameStatusOutput;

        public string Hunks(string baseCommit, IReadOnlyList<string> paths) => HunkOutput;

        public IEnumerable<string> HunksInChunks(string baseCommit, IReadOnlyList<string> paths) => [HunkOutput];

        public IReadOnlyList<string> UntrackedFiles() => Untracked;

        public string? ShowFile(string revision, string path) => null;

        public string? CurrentBranch() => "feature";

        public string? HeadCommit() => "head-commit";
    }
}
