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

    public IReadOnlyList<string> BuildArguments(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        if (selected.Count == 0)
        {
            return [];
        }

        var builder = new StringBuilder();

        foreach (var term in Terms(selected, allInProject))
        {
            if (builder.Length > 0)
            {
                builder.Append('|');
            }

            // `~` (contains) rather than `=` (equals): a data-driven test's reported name carries
            // its arguments - `Ns.Cls.Method(x: 1)` - which an equality filter would never match.
            // It is also what lets a class name stand for every test inside it.
            builder.Append("FullyQualifiedName~").Append(Escape(term));
        }

        return ["--filter", builder.ToString()];
    }

    /// <summary>
    /// The substrings the emitted filter matches on: one per collapsed class, one per remaining
    /// test.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="ExtraMatches"/> rather than reproduced there. It used to be
    /// reproduced, and the two drifted in the one case where they differ: a collapsed class emits
    /// <c>Ns.Cls.</c> while the residue was computed from the selected method names, so a nested
    /// class - whose reported name has its outer class's as a prefix - was matched by the filter
    /// and reported by nobody. Over-selection this tool cannot see is over-selection it cannot
    /// warn about.
    /// </remarks>
    private static List<string> Terms(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        var collapsed = ClassCollapser.Collapse(selected, allInProject);
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var test in collapsed.WholeClasses)
        {
            Add(ClassCollapser.FullClassName(test) + ".");
        }

        foreach (var test in collapsed.IndividualTests)
        {
            Add(test.FullyQualifiedName);
        }

        return terms;

        void Add(string term)
        {
            if (seen.Add(term))
            {
                terms.Add(term);
            }
        }
    }

    /// <summary>
    /// A contains-match also matches any test whose reported name has an emitted term as a
    /// substring - <c>Add</c> and <c>AddRange</c>, for instance, or every test of
    /// <c>Ns.Cls.Nested</c> when <c>Ns.Cls.</c> was collapsed. That is safe, but it is
    /// over-selection and gets reported.
    /// </summary>
    public IReadOnlyList<TestMethod> ExtraMatches(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        if (selected.Count == 0)
        {
            return [];
        }

        var selectedNames = new HashSet<string>(selected.Select(t => t.FullyQualifiedName), StringComparer.Ordinal);
        var terms = Terms(selected, allInProject);

        return [.. allInProject.Where(candidate =>
            !selectedNames.Contains(candidate.FullyQualifiedName) &&
            terms.Any(term => candidate.FullyQualifiedName.Contains(term, StringComparison.Ordinal)))];
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
