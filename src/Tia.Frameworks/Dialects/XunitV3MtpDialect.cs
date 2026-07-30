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

    public IReadOnlyList<string> BuildArguments(IReadOnlyList<TestMethod> tests)
    {
        var arguments = new List<string>(tests.Count * 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var test in tests)
        {
            if (!seen.Add(test.FullyQualifiedName))
            {
                continue;
            }

            arguments.Add("--filter-method");
            arguments.Add(test.FullyQualifiedName);
        }

        return arguments;
    }

    /// <summary><c>--filter-method</c> matches the whole method name exactly, so the only extra
    /// matches are the data cases of a parameterised test - which is the granularity selection
    /// deliberately works at.</summary>
    public IReadOnlyList<TestMethod> ExtraMatches(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject) => [];
}
