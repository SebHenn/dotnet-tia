using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tia.Core.Safety;

/// <summary>A line of C# whose string literal names a changed file.</summary>
public readonly record struct NamedFileLine(int Line, string Path);

/// <summary>
/// Finds the string literals that name a file by path, for a changed file no project owns (#168).
/// </summary>
/// <remarks>
/// <para>
/// No project holding a file does not mean nothing reads it. <c>docs/report.schema.json</c> is read
/// by this repository's own tests through <c>Path.Combine(Root, "docs", "report.schema.json")</c>,
/// and changing it selected no test while one failed. A <c>docker-compose.yml</c> or a
/// <c>db/schema.sql</c> has the same shape. Widening on every such file would give up the zero
/// selection a README edit earns, and a literal is the evidence that separates the two.
/// </para>
/// <para>
/// This narrows a miss and does not close it. A path assembled from a variable, read from
/// configuration or found by enumerating a directory names nothing here, so the file still selects
/// nothing, and the note saying so is what remains. Matching is deliberately loose in the other
/// direction: any file of that name anywhere counts, and a false match can only select more.
/// </para>
/// </remarks>
public static class NamedFileLiterals
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// True when <paramref name="literal"/> reads as a path to <paramref name="path"/>: one ends with
    /// the other, a whole segment at a time. Whichever is longer, because a literal can be relative
    /// to a folder below the root (<c>../db/schema.sql</c>) or absolute.
    /// </summary>
    /// <remarks>
    /// A literal has to reach the file name, so <c>"db"</c> does not name <c>db/schema.sql</c>. Reading
    /// a folder's name as naming everything in it would match <c>"docs"</c> and <c>"src"</c>, which are
    /// ordinary strings, and take a README edit from no tests to many. A URL is not a path in this
    /// repository even when it ends like one, as a schema's <c>$id</c> does.
    /// </remarks>
    public static bool Names(string literal, string path)
    {
        if (literal.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var named = Segments(literal);
        var actual = Segments(path);

        if (named.Length == 0 || actual.Length == 0)
        {
            return false;
        }

        for (var fromEnd = 1; fromEnd <= Math.Min(named.Length, actual.Length); fromEnd++)
        {
            // Case-insensitively on every platform: a false match only selects more.
            if (!string.Equals(named[^fromEnd], actual[^fromEnd], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The lines of <paramref name="tree"/> holding a literal that names one of
    /// <paramref name="paths"/>. Reads tokens rather than text, so a comment or a disabled region names
    /// nothing, and an escaped backslash reads as the backslash it is.
    /// </summary>
    public static IReadOnlyList<NamedFileLine> Find(
        SyntaxTree tree,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(paths);

        var found = new List<NamedFileLine>();

        foreach (var token in tree.GetRoot(cancellationToken).DescendantTokens())
        {
            if (!IsStringText(token))
            {
                continue;
            }

            foreach (var path in paths)
            {
                if (Names(token.ValueText, path))
                {
                    found.Add(new NamedFileLine(token.GetLocation().GetLineSpan().StartLinePosition.Line + 1, path));
                }
            }
        }

        return found;
    }

    private static bool IsStringText(SyntaxToken token) =>
        token.Kind() is SyntaxKind.StringLiteralToken
            or SyntaxKind.Utf8StringLiteralToken
            or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.Utf8SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken
            or SyntaxKind.Utf8MultiLineRawStringLiteralToken
            or SyntaxKind.InterpolatedStringTextToken;

    /// <summary>The path's segments, without the empty ones and the <c>.</c> and <c>..</c> that say nothing about which file it is.</summary>
    private static string[] Segments(string path) =>
        [.. path.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(s => s is not ("." or ".."))];
}
