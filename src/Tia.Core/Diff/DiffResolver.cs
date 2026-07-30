namespace Tia.Core.Diff;

public sealed record DiffResolution
{
    public DiffResult? Diff { get; init; }

    /// <summary>Non-null when the diff could not be resolved safely, in which case a full run is required.</summary>
    public string? FullRunReason { get; init; }

    public bool IsResolved => Diff is not null;
}

/// <summary>
/// Turns a base revision into a set of changed files with line ranges on both sides.
/// </summary>
/// <remarks>
/// The diff is taken against the working tree rather than HEAD, so uncommitted edits are analysed
/// too - which is what you want both locally and in a CI job that has just applied a patch.
/// </remarks>
public sealed class DiffResolver(IGitClient git)
{
    private static readonly LineRange WholeFile = new(1, int.MaxValue);

    /// <summary>
    /// Build output is derived, never a cause. A repository that does not ignore <c>bin/</c> and
    /// <c>obj/</c> would otherwise force a full run on every analysis, because the generated
    /// <c>.props</c> files under <c>obj/</c> look exactly like build inputs.
    /// </summary>
    private static bool IsBuildOutput(string path) =>
        path.StartsWith("bin/", StringComparison.Ordinal) ||
        path.StartsWith("obj/", StringComparison.Ordinal) ||
        path.Contains("/bin/", StringComparison.Ordinal) ||
        path.Contains("/obj/", StringComparison.Ordinal);

    public DiffResolution Resolve(string baseRef)
    {
        var baseCommit = git.ResolveCommit(baseRef);
        if (baseCommit is null)
        {
            return new DiffResolution
            {
                FullRunReason = git.IsShallow
                    ? $"base revision '{baseRef}' is not reachable - the clone is shallow, so history was truncated"
                    : $"base revision '{baseRef}' could not be resolved to a commit",
            };
        }

        var head = git.HeadCommit();
        if (head is null)
        {
            return new DiffResolution { FullRunReason = "HEAD could not be resolved (empty repository?)" };
        }

        // A base that is not an ancestor means the branch has diverged and the diff would include
        // other people's work in the wrong direction. Use the merge base instead, and bail out if
        // there isn't one.
        var effectiveBase = baseCommit;
        if (!git.IsAncestor(baseCommit, head))
        {
            var mergeBase = git.MergeBase(baseCommit, head);
            if (mergeBase is null)
            {
                return new DiffResolution
                {
                    FullRunReason = git.IsShallow
                        ? $"no merge base between '{baseRef}' and HEAD - the clone is shallow"
                        : $"no merge base between '{baseRef}' and HEAD",
                };
            }

            effectiveBase = mergeBase;
        }

        var files = GitDiffParser.ParseNameStatus(git.NameStatus(effectiveBase))
            .Where(f => !IsBuildOutput(f.Path))
            .ToList();

        // A file that was written but never staged is not in any diff, yet it is very much a
        // change - a new test, a new fixture, a new data file.
        var tracked = new HashSet<string>(files.Select(f => f.Path), StringComparer.Ordinal);
        foreach (var untracked in git.UntrackedFiles())
        {
            if (!IsBuildOutput(untracked) && tracked.Add(untracked))
            {
                files.Add(new ChangedFile { Path = untracked, Kind = FileChangeKind.Added });
            }
        }

        var resolved = new List<ChangedFile>(files.Count);

        foreach (var file in files)
        {
            if (!file.IsCSharp)
            {
                // Line ranges are only meaningful for files we parse. Everything else is handled
                // wholesale by the safety rules.
                resolved.Add(file);
                continue;
            }

            var paths = file.OldPath is null || file.OldPath == file.Path
                ? new[] { file.Path }
                : [file.OldPath, file.Path];

            var (oldRanges, newRanges) = GitDiffParser.ParseHunks(git.Hunks(effectiveBase, paths));

            // An untracked file produces no hunks at all, so treat a new file without them as
            // changed end to end rather than as changed nowhere.
            if (newRanges.Count == 0 && file.Kind == FileChangeKind.Added)
            {
                newRanges = [WholeFile];
            }

            resolved.Add(file with { OldLines = oldRanges, NewLines = newRanges });
        }

        return new DiffResolution
        {
            Diff = new DiffResult { BaseRef = baseRef, BaseCommit = effectiveBase, Files = resolved },
        };
    }
}
