using Tia.Cli;

namespace Tia.Integration.Tests;

/// <summary>
/// The base a command diffs against when <c>--base</c> is not given (#126). It used to be the
/// literal <c>origin/main</c>, so on a <c>master</c> or <c>trunk</c> repository a bare
/// <c>dotnet tia run</c> became a full run on the first try.
/// </summary>
public sealed class DefaultBaseTests : IDisposable
{
    private readonly string _root = Path.Combine(FixtureRepository.RealTempDirectory(), "tia-base-" + Guid.NewGuid().ToString("n"));

    public DefaultBaseTests()
    {
        Directory.CreateDirectory(_root);
        TestGit.Init(_root);
        File.WriteAllText(Path.Combine(_root, "README.md"), "base\n");
        TestGit.Run(_root, "add", "-A");
        TestGit.Run(_root, "commit", "-m", "first");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void The_remote_default_branch_is_followed()
    {
        TestGit.Run(_root, "update-ref", "refs/remotes/origin/trunk", "HEAD");
        TestGit.Run(_root, "update-ref", "refs/remotes/origin/main", "HEAD");
        TestGit.Run(_root, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/trunk");

        Assert.Equal(new ResolvedBase("origin/trunk", null), DefaultBase.Resolve(_root));
    }

    [Fact]
    public void Without_origin_HEAD_master_is_tried_after_main()
    {
        TestGit.Run(_root, "update-ref", "refs/remotes/origin/master", "HEAD");

        Assert.Equal(new ResolvedBase("origin/master", null), DefaultBase.Resolve(_root));
    }

    [Fact]
    public void Without_any_candidate_the_reason_says_what_to_do()
    {
        var resolved = DefaultBase.Resolve(_root);

        Assert.Equal("origin/main", resolved.Ref);
        Assert.Contains("git remote set-head origin -a", resolved.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_given_base_is_taken_as_given_and_a_command_without_one_asks_nothing()
    {
        TestGit.Run(_root, "update-ref", "refs/remotes/origin/trunk", "HEAD");
        TestGit.Run(_root, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/trunk");
        var common = new CommonOptions();
        var root = Program.BuildRoot(common);

        var defaulted = common.Read(root.Parse(["analyze", "--path", _root]), log: null);
        var given = common.Read(root.Parse(["analyze", "--path", _root, "--base", "v1.0"]), log: null);

        Assert.Equal("origin/trunk", defaulted.BaseRef);
        Assert.Equal("v1.0", given.BaseRef);
        Assert.Null(given.BaseRefHint);
    }
}
