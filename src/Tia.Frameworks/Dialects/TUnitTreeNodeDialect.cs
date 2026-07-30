using System.Text;
using Tia.Core.Model;

namespace Tia.Frameworks.Dialects;

/// <summary>
/// TUnit on Microsoft.Testing.Platform, filtered with
/// <c>--treenode-filter "/*/Namespace/Class/Method"</c>.
/// </summary>
/// <remarks>
/// The tree-node grammar alternates *within* a path segment, not across whole paths, so a
/// selection spanning several classes can only be expressed as the cross product of its segments.
/// That is a superset of the selection - never a subset - and the extra matches are reported as a
/// widening rather than hidden.
/// </remarks>
public sealed class TUnitTreeNodeDialect : IFilterDialect
{
    public string Name => "tunit-treenode";

    public IReadOnlyList<string> BuildArguments(IReadOnlyList<TestMethod> tests, IReadOnlyList<TestMethod> allInProject)
    {
        if (tests.Count == 0)
        {
            return [];
        }

        var namespaces = Distinct(tests.Select(t => t.Namespace.Length == 0 ? "*" : t.Namespace));
        var classes = Distinct(tests.Select(t => t.ClassName));
        var methods = Distinct(tests.Select(t => t.MethodName));

        var filter = new StringBuilder()
            .Append("/*/")
            .Append(Alternation(namespaces))
            .Append('/')
            .Append(Alternation(classes))
            .Append('/')
            .Append(Alternation(methods))
            .ToString();

        return ["--treenode-filter", filter];
    }

    public IReadOnlyList<TestMethod> ExtraMatches(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        if (selected.Count == 0)
        {
            return [];
        }

        var namespaces = new HashSet<string>(selected.Select(t => t.Namespace), StringComparer.Ordinal);
        var classes = new HashSet<string>(selected.Select(t => t.ClassName), StringComparer.Ordinal);
        var methods = new HashSet<string>(selected.Select(t => t.MethodName), StringComparer.Ordinal);
        var selectedNames = new HashSet<string>(selected.Select(t => t.FullyQualifiedName), StringComparer.Ordinal);

        var extra = new List<TestMethod>();
        foreach (var candidate in allInProject)
        {
            if (selectedNames.Contains(candidate.FullyQualifiedName))
            {
                continue;
            }

            if (namespaces.Contains(candidate.Namespace) &&
                classes.Contains(candidate.ClassName) &&
                methods.Contains(candidate.MethodName))
            {
                extra.Add(candidate);
            }
        }

        return extra;
    }

    private static List<string> Distinct(IEnumerable<string> values) =>
        [.. values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)];

    private static string Alternation(List<string> values) =>
        values.Count == 1 ? Escape(values[0]) : string.Join('|', values.Select(Escape));

    internal static string Escape(string value)
    {
        if (value == "*")
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '/' or '*' or '(' or ')' or '[' or ']' or '|' or '&' or ',' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
