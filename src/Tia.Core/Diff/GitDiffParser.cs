using System.Globalization;

namespace Tia.Core.Diff;

/// <summary>
/// Parses the two git outputs the engine relies on: NUL-separated <c>--name-status</c> and the
/// hunk headers of <c>-U0</c> unified diffs. Kept free of process launching so it can be unit
/// tested against captured git output.
/// </summary>
public static class GitDiffParser
{
    /// <summary>
    /// Parses the output of <c>git diff --name-status -M -z</c>. The NUL-separated form is used
    /// because paths may contain spaces, quotes or tabs, which the default output mangles.
    /// </summary>
    public static IReadOnlyList<ChangedFile> ParseNameStatus(string output)
    {
        // NUL-separated output carries no newlines of its own, so any that survive came from
        // however the process output was captured. Left in place they become a phantom entry
        // whose path is a bare newline.
        var fields = output.Split('\0', StringSplitOptions.None)
            .Select(f => f.Trim('\n', '\r'))
            .ToArray();

        var results = new List<ChangedFile>();

        for (var i = 0; i < fields.Length; i++)
        {
            var status = fields[i];
            if (string.IsNullOrEmpty(status))
            {
                continue;
            }

            var kind = ParseStatus(status[0]);
            var takesTwoPaths = kind is FileChangeKind.Renamed or FileChangeKind.Copied;

            if (i + 1 >= fields.Length)
            {
                break;
            }

            if (takesTwoPaths)
            {
                if (i + 2 >= fields.Length)
                {
                    break;
                }

                var oldPath = Normalize(fields[i + 1]);
                var newPath = Normalize(fields[i + 2]);
                i += 2;
                results.Add(new ChangedFile { Path = newPath, OldPath = oldPath, Kind = kind });
            }
            else
            {
                var path = Normalize(fields[i + 1]);
                i += 1;
                results.Add(new ChangedFile { Path = path, Kind = kind });
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts changed line ranges from the <c>@@ -a,b +c,d @@</c> headers of a unified diff.
    /// With <c>-U0</c> there is no context, so each hunk is exactly a changed region.
    /// </summary>
    /// <summary>
    /// Splits a multi-file unified diff into per-file line ranges, keyed by both sides' paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ParseHunks"/> ignores the file headers and collects every hunk it sees, which is
    /// correct for a single-file diff and silently wrong for any other - so the caller ran one
    /// <c>git diff</c> per changed file. On a ten-file change that was ten process spawns and most
    /// of the measured diff cost.
    /// </para>
    /// <para>
    /// Keyed by the new path and the old one both, pointing at the same entry, because a rename
    /// under <c>-M</c> arrives as one block naming two paths and the caller may look up either.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, (IReadOnlyList<LineRange> Old, IReadOnlyList<LineRange> New)>
        ParseHunksByFile(string unifiedDiff)
    {
        ArgumentNullException.ThrowIfNull(unifiedDiff);

        var byFile = new Dictionary<string, (List<LineRange> Old, List<LineRange> New)>(StringComparer.Ordinal);
        var current = default((List<LineRange> Old, List<LineRange> New)?);

        foreach (var raw in unifiedDiff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // A new block resets the target, so a header this does not recognise cannot silently
            // pour its hunks into the previous file's ranges.
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }

            if (PathOf(line, "--- ") is { } oldPath)
            {
                current = Entry(byFile, oldPath, current);
                continue;
            }

            if (PathOf(line, "+++ ") is { } newPath)
            {
                current = Entry(byFile, newPath, current);
                continue;
            }

            if (current is not { } target || !line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (sign, range) in HunkRanges(line))
            {
                (sign == '-' ? target.Old : target.New).Add(range);
            }
        }

        return byFile.ToDictionary(
            pair => pair.Key,
            pair => ((IReadOnlyList<LineRange>)pair.Value.Old, (IReadOnlyList<LineRange>)pair.Value.New),
            StringComparer.Ordinal);

        // Both sides of one block share the entry, so a rename's old and new ranges land together
        // whichever path the caller asks for.
        static (List<LineRange>, List<LineRange>) Entry(
            Dictionary<string, (List<LineRange> Old, List<LineRange> New)> byFile,
            string path,
            (List<LineRange> Old, List<LineRange> New)? existing)
        {
            if (byFile.TryGetValue(path, out var found))
            {
                return found;
            }

            var entry = existing ?? ([], []);
            byFile[path] = entry;
            return entry;
        }
    }

    /// <summary>
    /// The repository-relative path a <c>---</c>/<c>+++</c> header names, or null when it names
    /// nothing - <c>/dev/null</c> for an add or a delete, or a line that merely starts the same way.
    /// </summary>
    private static string? PathOf(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var value = line[prefix.Length..];
        if (value is "/dev/null")
        {
            return null;
        }

        // `a/` and `b/` are git's prefixes, not part of the path. Everything after the first slash
        // is the path verbatim, including spaces - git puts nothing else on these lines.
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 && slash + 1 < value.Length ? value[(slash + 1)..] : null;
    }

    private static IEnumerable<(char Sign, LineRange Range)> HunkRanges(string line)
    {
        var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
        if (end < 0)
        {
            yield break;
        }

        foreach (var part in line[2..end].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 2 && (part[0] == '-' || part[0] == '+') && TryParseRange(part[1..], out var range))
            {
                yield return (part[0], range);
            }
        }
    }

    public static (IReadOnlyList<LineRange> Old, IReadOnlyList<LineRange> New) ParseHunks(string unifiedDiff)
    {
        var oldRanges = new List<LineRange>();
        var newRanges = new List<LineRange>();

        foreach (var line in unifiedDiff.Split('\n'))
        {
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var header = line[2..end];
            foreach (var part in header.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length < 2)
                {
                    continue;
                }

                var sign = part[0];
                if (sign != '-' && sign != '+')
                {
                    continue;
                }

                if (!TryParseRange(part[1..], out var range))
                {
                    continue;
                }

                (sign == '-' ? oldRanges : newRanges).Add(range);
            }
        }

        return (oldRanges, newRanges);
    }

    private static bool TryParseRange(string text, out LineRange range)
    {
        range = default;

        var comma = text.IndexOf(',', StringComparison.Ordinal);
        var startText = comma < 0 ? text : text[..comma];
        var countText = comma < 0 ? "1" : text[(comma + 1)..];

        if (!int.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return false;
        }

        if (count == 0)
        {
            // A zero-length hunk is an insertion point: git reports the line *before* the
            // insertion, so the affected line on this side is the one that follows it.
            range = new LineRange(Math.Max(1, start), Math.Max(1, start + 1));
            return true;
        }

        range = new LineRange(start, start + count - 1);
        return true;
    }

    private static FileChangeKind ParseStatus(char c) => c switch
    {
        'A' => FileChangeKind.Added,
        'D' => FileChangeKind.Deleted,
        'R' => FileChangeKind.Renamed,
        'C' => FileChangeKind.Copied,
        'T' => FileChangeKind.TypeChanged,
        _ => FileChangeKind.Modified,
    };

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
