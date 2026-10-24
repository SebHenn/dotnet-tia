namespace Tia.Core.Diff;

public sealed record DiffResolution
{
    public DiffResult? Diff { get; init; }

    /// <summary>Non-null when the diff could not be resolved safely, in which case a full run is required.</summary>
    public string? FullRunReason { get; init; }
}

/// <summary>
/// Turns a base revision into a set of changed files with line ranges on both sides.
/// </summary>
/// <remarks>
/// The diff is taken against the working tree rather than HEAD, so uncommitted edits are analysed
/// too - which is what you want both locally and in a CI job that has just applied a patch.
/// </remarks>
/// <param name="cacheDirectory">
/// The cache directory relative to the repository root, when it is inside it. Left out of the diff
/// like <c>.tia/</c>, which is only the default (#44).
/// </param>
public sealed class DiffResolver(IGitClient git, string? cacheDirectory = null)
{
    private static readonly LineRange WholeFile = new(1, int.MaxValue);

    private readonly Dictionary<string, bool> _holdsProjectFile = new(StringComparer.Ordinal);

    /// <summary>
    /// Derived output is never a cause of anything.
    /// </summary>
    /// <remarks>
    /// A repository that does not ignore <c>bin/</c> and <c>obj/</c> would otherwise force a full
    /// run on every analysis, because the generated <c>.props</c> files under <c>obj/</c> look
    /// exactly like build inputs. <c>.tia/</c> matters even more: it is this tool's own graph
    /// cache, so without this the first run writes a file that the second run reports as a change.
    /// </remarks>
    private static bool IsDerivedOutput(string path) =>
        HasSegment(path, "bin") || HasSegment(path, "obj") || HasSegment(path, ".tia");

    /// <summary>
    /// The same rule for a file git tracks, which is held to what the SDK itself leaves out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK excludes only a project's own <c>bin/</c> and <c>obj/</c> from compilation. A
    /// folder of that name anywhere else is source: <c>src/App/Feature/obj/Parser.cs</c> is
    /// compiled, and a <c>.props</c> beneath it is imported. Dropping every such path made a
    /// change there no change at all, so its tests were never selected (#54).
    /// </para>
    /// <para>
    /// Tracked files only, because only they can arrive in a pull request. An untracked file is
    /// local, and that is where build output lands in a repository that does not ignore it, under
    /// whatever layout a <c>Directory.Build.props</c> chose.
    /// </para>
    /// </remarks>
    private bool IsTrackedDerivedOutput(string path)
    {
        if (HasSegment(path, ".tia") || IsInCacheDirectory(path))
        {
            return true;
        }

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i] is "bin" or "obj" && HoldsProjectFile(string.Join('/', segments[..i])))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInCacheDirectory(string path) =>
        cacheDirectory is { Length: > 0 } cache && path.StartsWith(cache + "/", StringComparison.Ordinal);

    private bool HoldsProjectFile(string directory)
    {
        if (!_holdsProjectFile.TryGetValue(directory, out var holds))
        {
            var full = Path.Combine(git.RepositoryRoot, directory);
            holds = Directory.Exists(full) && Directory.EnumerateFiles(full, "*.*proj").Any();
            _holdsProjectFile[directory] = holds;
        }

        return holds;
    }

    private static bool HasSegment(string path, string segment) =>
        path.StartsWith(segment + "/", StringComparison.Ordinal) ||
        path.Contains("/" + segment + "/", StringComparison.Ordinal);

    /// <summary>
    /// Every changed C# file's line ranges, from as few <c>git diff</c> calls as possible.
    /// </summary>
    private static IReadOnlyDictionary<string, (IReadOnlyList<LineRange> Old, IReadOnlyList<LineRange> New)> HunksFor(
        IGitClient git,
        string effectiveBase,
        IReadOnlyList<ChangedFile> files)
    {
        var paths = new List<string>();

        foreach (var file in files.Where(f => f.IsCSharp))
        {
            paths.Add(file.Path);

            if (file.OldPath is { } old && old != file.Path)
            {
                paths.Add(old);
            }
        }

        if (paths.Count == 0)
        {
            return new Dictionary<string, (IReadOnlyList<LineRange>, IReadOnlyList<LineRange>)>(StringComparer.Ordinal);
        }

        var combined = new Dictionary<string, (IReadOnlyList<LineRange> Old, IReadOnlyList<LineRange> New)>(StringComparer.Ordinal);

        foreach (var chunk in git.HunksInChunks(effectiveBase, paths))
        {
            foreach (var (path, ranges) in GitDiffParser.ParseHunksByFile(chunk))
            {
                combined[path] = ranges;
            }
        }

        return combined;
    }

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
            // A rename is dropped only when both of its sides are output: one that moves a source
            // file into obj/ is still the deletion of that source file.
            .Where(f => !IsTrackedDerivedOutput(f.Path) || (f.OldPath is { } old && !IsTrackedDerivedOutput(old)))
            .ToList();

        // A file that was written but never staged is not in any diff, yet it is very much a
        // change - a new test, a new fixture, a new data file.
        var tracked = new HashSet<string>(files.Select(f => f.Path), StringComparer.Ordinal);
        foreach (var untracked in git.UntrackedFiles())
        {
            if (!IsDerivedOutput(untracked) && !IsInCacheDirectory(untracked) && tracked.Add(untracked))
            {
                files.Add(new ChangedFile { Path = untracked, Kind = FileChangeKind.Added });
            }
        }

        var resolved = new List<ChangedFile>(files.Count);
        var hunks = HunksFor(git, effectiveBase, files);

        foreach (var file in files)
        {
            if (!file.IsCSharp)
            {
                // Line ranges are only meaningful for files we parse. Everything else is handled
                // wholesale by the safety rules.
                resolved.Add(file);
                continue;
            }

            // Absent means the diff named no hunks for it. For a modification that should not
            // happen, and the conservative reading is the whole file rather than nothing: an
            // attribution defect then costs an over-selection instead of a missed test.
            var found = hunks.TryGetValue(file.Path, out var ranges) ||
                        (file.OldPath is { } old && hunks.TryGetValue(old, out ranges));

            var (oldRanges, newRanges) = found
                ? ranges
                : (file.Kind == FileChangeKind.Added ? [] : (IReadOnlyList<LineRange>)[WholeFile],
                   (IReadOnlyList<LineRange>)[WholeFile]);

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
