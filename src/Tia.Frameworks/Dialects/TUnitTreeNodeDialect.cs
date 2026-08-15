using System.Text;
using Tia.Core.Model;

namespace Tia.Frameworks.Dialects;

/// <summary>
/// TUnit on Microsoft.Testing.Platform, filtered with
/// <c>--treenode-filter "/*/Namespace/Class/Method"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The tree-node grammar alternates *within* a path segment, not across whole paths, so a selection
/// spanning several classes can only be expressed as the cross product of its segments. That is a
/// superset of the selection - never a subset - and the extra matches are reported as a widening
/// rather than hidden.
/// </para>
/// <para>
/// Three properties of the grammar were established by running the real runner against
/// <c>tests/Tia.Fixtures.Tunit</c>, because each of them looks like it could go the other way and
/// guessing wrong is how you get a miss:
/// </para>
/// <list type="bullet">
/// <item><c>--treenode-filter</c> is <b>not</b> repeatable. Passing it twice selects
/// <b>zero</b> tests, rather than the union - so the shape <see cref="XunitV3MtpDialect"/> uses is
/// not available here.</item>
/// <item>A union of whole paths cannot be spelled. <c>/*/Ns/A/*|/*/Ns/B/m</c> parses as an
/// alternation inside the *method* segment of the first path, and quietly matches only class A -
/// a subset, which is the one kind of wrong answer that costs a missed test. The runner says as
/// much when it refuses the parenthesised form: alternatives combine within a segment, not across.
/// </item>
/// <item>A class alternation with a wildcard method - <c>/*/Ns/(A|B)/*</c> - does work.</item>
/// </list>
/// <para>
/// That last one is the only win available, and it is a win in filter *length*, not in precision:
/// collapsing to <c>/Class/*</c> drops the method constraint, so it can only ever match more. It is
/// used exactly where it costs nothing - when every selected class is selected whole, the
/// cross product would match those classes entirely anyway - and length is worth having on its own,
/// because a filter that outgrows the command line is abandoned and the project runs whole.
/// </para>
/// </remarks>
public sealed class TUnitTreeNodeDialect : IFilterDialect
{
    public string Name => "tunit-treenode";

    /// <summary>
    /// The emitted filter, and the predicate describing what it matches. The two are built together
    /// because they have to agree: <see cref="ExtraMatches"/> reporting the residue of a filter
    /// other than the one emitted would understate the widening precisely when it grew.
    /// </summary>
    private sealed record Shape(string Filter, Func<TestMethod, bool> Matches);

    private static Shape? Build(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        if (selected.Count == 0)
        {
            return null;
        }

        var namespaces = Distinct(selected.Select(t => t.Namespace.Length == 0 ? "*" : t.Namespace));
        var classes = Distinct(selected.Select(t => t.ClassName));

        var namespaceSet = new HashSet<string>(selected.Select(t => t.Namespace), StringComparer.Ordinal);
        var classSet = new HashSet<string>(classes, StringComparer.Ordinal);

        var prefix = new StringBuilder()
            .Append("/*/")
            .Append(Alternation(namespaces))
            .Append('/')
            .Append(Alternation(classes))
            .Append('/')
            .ToString();

        // Every selected class selected whole: the method alternation would list every method those
        // classes have, so `*` matches exactly the same tests in a fraction of the characters.
        if (AllClassesWhollySelected(selected, allInProject))
        {
            return new Shape(
                prefix + "*",
                t => namespaceSet.Contains(t.Namespace) && classSet.Contains(t.ClassName));
        }

        var methods = Distinct(selected.Select(t => t.MethodName));
        var methodSet = new HashSet<string>(methods, StringComparer.Ordinal);

        return new Shape(
            prefix + Alternation(methods),
            t => namespaceSet.Contains(t.Namespace) &&
                 classSet.Contains(t.ClassName) &&
                 methodSet.Contains(t.MethodName));
    }

    /// <summary>
    /// Whether every class the selection touches is selected in full. Uses
    /// <see cref="ClassCollapser"/> so this dialect and the VSTest ones agree on what "whole class"
    /// means - including its rule that a one-test class is not worth collapsing.
    /// </summary>
    private static bool AllClassesWhollySelected(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        var collapsed = ClassCollapser.Collapse(selected, allInProject);
        return collapsed.WholeClasses.Count > 0 && collapsed.IndividualTests.Count == 0;
    }

    public IReadOnlyList<string> BuildArguments(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject) =>
        Build(selected, allInProject) is { } shape ? ["--treenode-filter", shape.Filter] : [];

    public IReadOnlyList<TestMethod> ExtraMatches(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        if (Build(selected, allInProject) is not { } shape)
        {
            return [];
        }

        var selectedNames = new HashSet<string>(selected.Select(t => t.FullyQualifiedName), StringComparer.Ordinal);

        return [.. allInProject.Where(c => !selectedNames.Contains(c.FullyQualifiedName) && shape.Matches(c))];
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
