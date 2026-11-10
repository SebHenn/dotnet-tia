using System.Text;
using System.Text.RegularExpressions;

namespace Tia.Core.Configuration;

/// <summary>
/// A glob over repository paths, as <c>fullRunPaths</c> and <c>ignorePaths</c> in <c>tia.json</c>
/// write them (#170).
/// </summary>
/// <remarks>
/// <para>
/// The rules are the ones a <c>.gitignore</c> reader already knows, minus negation: <c>*</c> and
/// <c>?</c> stay inside one path segment, <c>**</c> crosses them, a pattern with no slash matches a
/// name at any depth, one with a slash is anchored at the directory holding <c>tia.json</c>, and a
/// pattern that matches a directory matches everything below it. So <c>db</c>, <c>db/</c> and
/// <c>db/**</c> all cover <c>db/schema.sql</c>.
/// </para>
/// <para>
/// Case-sensitive, as git is. A pattern that differs from the path only in case matches nothing,
/// which <see cref="TiaConfig"/> cannot see; the report lists every file a pattern did match.
/// </para>
/// </remarks>
public sealed class PathPattern
{
    private readonly Regex _regex;

    private PathPattern(string text, Regex regex)
    {
        Text = text;
        _regex = regex;
    }

    /// <summary>The pattern as written in the file.</summary>
    public string Text { get; }

    /// <summary>The pattern, or null with <paramref name="error"/> set when it cannot be one.</summary>
    public static PathPattern? Parse(string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);

        var pattern = text.Trim().Replace('\\', '/');
        error = pattern switch
        {
            "" or "/" => "is empty",
            _ when pattern.StartsWith('!') => "starts with '!', and negation is not supported: list only what the pattern should match",
            _ when pattern.Split('/').Any(s => s is "." or "..") => "contains '.' or '..' as a segment, and paths are matched as git writes them",
            _ => null,
        };

        if (error is not null)
        {
            return null;
        }

        if (pattern.EndsWith('/'))
        {
            pattern = pattern[..^1];
        }

        // A slash anywhere but the end anchors the pattern; without one it names a file or
        // directory at any depth.
        pattern = pattern.StartsWith('/')
            ? pattern[1..]
            : pattern.Contains('/', StringComparison.Ordinal) ? pattern : "**/" + pattern;

        return new PathPattern(text, new Regex(
            "^" + ToRegex(pattern) + "$",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Whether the pattern covers <paramref name="path"/>: the path itself or any directory above
    /// it. The path is relative, with forward slashes.
    /// </summary>
    public bool Matches(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_regex.IsMatch(path))
        {
            return true;
        }

        for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (_regex.IsMatch(path.AsSpan(0, slash)))
            {
                return true;
            }
        }

        return false;
    }

    public override string ToString() => Text;

    private static string ToRegex(string pattern)
    {
        var regex = new StringBuilder();
        var segments = pattern.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;

            if (segments[i] == "**")
            {
                // Zero or more whole segments, so `a/**/b` also matches `a/b`, and a trailing
                // `**` matches whatever lies below.
                regex.Append(last ? ".*" : "(.*/)?");
                continue;
            }

            foreach (var c in segments[i])
            {
                regex.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }

            if (!last)
            {
                regex.Append('/');
            }
        }

        return regex.ToString();
    }
}

/// <summary>
/// A test name in <c>alwaysRun</c>: a fully qualified test, or a class or namespace that contains
/// it, with <c>*</c> for any run of characters (#170).
/// </summary>
public sealed class TestNamePattern
{
    private readonly Regex _regex;

    private TestNamePattern(string text, Regex regex)
    {
        Text = text;
        _regex = regex;
    }

    public string Text { get; }

    public static TestNamePattern? Parse(string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);

        var pattern = text.Trim();
        error = pattern.Length == 0
            ? "is empty"
            : pattern.Any(char.IsWhiteSpace)
                ? "contains white space, and no test name does"
                : null;

        if (error is not null)
        {
            return null;
        }

        var regex = "^" + string.Concat(pattern.Select(c => c == '*' ? ".*" : Regex.Escape(c.ToString()))) + "$";
        return new TestNamePattern(text, new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Whether the pattern names <paramref name="fullyQualifiedName"/>, or a namespace or class it
    /// is in: <c>Web.Tests.Smoke</c> matches <c>Web.Tests.Smoke.Loads</c> but not
    /// <c>Web.Tests.SmokeTests.Loads</c>.
    /// </summary>
    public bool Matches(string fullyQualifiedName)
    {
        ArgumentNullException.ThrowIfNull(fullyQualifiedName);

        if (_regex.IsMatch(fullyQualifiedName))
        {
            return true;
        }

        for (var i = 0; i < fullyQualifiedName.Length; i++)
        {
            if (fullyQualifiedName[i] is '.' or '+' && _regex.IsMatch(fullyQualifiedName.AsSpan(0, i)))
            {
                return true;
            }
        }

        return false;
    }

    public override string ToString() => Text;
}
