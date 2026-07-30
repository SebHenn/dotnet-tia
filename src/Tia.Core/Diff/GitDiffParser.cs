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

        var comma = text.IndexOf(',');
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
