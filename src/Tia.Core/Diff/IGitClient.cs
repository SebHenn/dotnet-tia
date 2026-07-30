namespace Tia.Core.Diff;

/// <summary>
/// The git surface the engine needs. Deliberately narrow, and deliberately shelling out to the
/// <c>git</c> executable rather than taking a LibGit2Sharp dependency: a global tool that carries
/// native binaries needs RID-specific packaging, which is a poor trade for four commands.
/// </summary>
public interface IGitClient
{
    string RepositoryRoot { get; }

    bool IsShallow { get; }

    /// <summary>Resolves a revision to a full commit id, or null when it cannot be reached.</summary>
    string? ResolveCommit(string revision);

    /// <summary>Returns the merge base of two commits, or null when they share no history.</summary>
    string? MergeBase(string a, string b);

    bool IsAncestor(string ancestor, string descendant);

    /// <summary>Raw output of <c>git diff --name-status -M -z &lt;baseCommit&gt;</c>.</summary>
    string NameStatus(string baseCommit);

    /// <summary>
    /// Raw output of <c>git diff -U0 -M &lt;baseCommit&gt; -- &lt;paths&gt;</c>. Renames need both the
    /// old and the new path in the pathspec or git reports the change as an unrelated add.
    /// </summary>
    string Hunks(string baseCommit, IReadOnlyList<string> paths);

    /// <summary>
    /// Paths that exist in the working tree but not in the index, excluding ignored files.
    /// <c>git diff</c> never reports these, so a newly written file would otherwise be invisible.
    /// </summary>
    IReadOnlyList<string> UntrackedFiles();

    /// <summary>Contents of a file at a revision, or null when the path does not exist there.</summary>
    string? ShowFile(string revision, string path);

    string? CurrentBranch();

    string? HeadCommit();
}
