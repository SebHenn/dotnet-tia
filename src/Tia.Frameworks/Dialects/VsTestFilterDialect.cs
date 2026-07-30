using System.Text;
using Tia.Core.Model;

namespace Tia.Frameworks.Dialects;

/// <summary>
/// The VSTest expression dialect: <c>--filter "FullyQualifiedName~A|FullyQualifiedName~B"</c>.
/// Used by xUnit v2, by xUnit v3 over the VSTest bridge, and by NUnit and MSTest under either
/// runner - three of the four supported frameworks share it.
/// </summary>
public sealed class VsTestFilterDialect : IFilterDialect
{
    public string Name => "vstest";

    public IReadOnlyList<string> BuildArguments(IReadOnlyList<TestMethod> tests)
    {
        if (tests.Count == 0)
        {
            return [];
        }

        var builder = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var test in tests)
        {
            if (!seen.Add(test.FullyQualifiedName))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('|');
            }

            // `~` (contains) rather than `=` (equals): a data-driven test's reported name carries
            // its arguments - `Ns.Cls.Method(x: 1)` - which an equality filter would never match.
            builder.Append("FullyQualifiedName~").Append(Escape(test.FullyQualifiedName));
        }

        return ["--filter", builder.ToString()];
    }

    /// <summary>
    /// A contains-match also matches any test whose reported name has a selected name as a
    /// substring - <c>Add</c> and <c>AddRange</c>, for instance. That is safe, but it is
    /// over-selection and gets reported.
    /// </summary>
    public IReadOnlyList<TestMethod> ExtraMatches(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        var selectedNames = new HashSet<string>(selected.Select(t => t.FullyQualifiedName), StringComparer.Ordinal);
        var extra = new List<TestMethod>();

        foreach (var candidate in allInProject)
        {
            if (selectedNames.Contains(candidate.FullyQualifiedName))
            {
                continue;
            }

            foreach (var name in selectedNames)
            {
                if (candidate.FullyQualifiedName.Contains(name, StringComparison.Ordinal))
                {
                    extra.Add(candidate);
                    break;
                }
            }
        }

        return extra;
    }

    internal static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
