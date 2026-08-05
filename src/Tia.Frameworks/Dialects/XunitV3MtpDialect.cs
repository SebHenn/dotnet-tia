using Tia.Core.Model;

namespace Tia.Frameworks.Dialects;

/// <summary>
/// xUnit v3 running natively on Microsoft.Testing.Platform. Its runner takes
/// <c>--filter-method Ns.Cls.Method</c>, repeatable and OR-ed - deliberately not VSTest syntax,
/// which the native runner does not understand.
/// </summary>
public sealed class XunitV3MtpDialect : IFilterDialect
{
    public string Name => "xunit-v3-mtp";

    public IReadOnlyList<string> BuildArguments(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        var arguments = new List<string>(selected.Count * 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var collapsed = ClassCollapser.Collapse(selected, allInProject);

        // Filters of the *same* kind are OR-ed, but two different kinds are AND-ed - so
        // `--filter-class A --filter-method B.C` means "tests in A that are also B.C", which is
        // empty. Class filters are therefore only usable when they can express the whole
        // selection on their own. Verified against the runner; the argument list alone looks
        // perfectly reasonable either way.
        if (collapsed.IndividualTests.Count == 0 && collapsed.WholeClasses.Count > 0)
        {
            foreach (var test in collapsed.WholeClasses)
            {
                var name = ClassCollapser.FullClassName(test);
                if (seen.Add(name))
                {
                    arguments.Add("--filter-class");
                    arguments.Add(name);
                }
            }

            return arguments;
        }

        foreach (var test in selected)
        {
            if (seen.Add(test.FullyQualifiedName))
            {
                arguments.Add("--filter-method");
                arguments.Add(test.FullyQualifiedName);
            }
        }

        return arguments;
    }

    /// <summary><c>--filter-method</c> matches the whole method name exactly, so the only extra
    /// matches are the data cases of a parameterised test - which is the granularity selection
    /// deliberately works at.</summary>
    public IReadOnlyList<TestMethod> ExtraMatches(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject) => [];
}
