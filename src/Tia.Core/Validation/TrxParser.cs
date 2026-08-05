using System.Text;
using System.Xml.Linq;

namespace Tia.Core.Validation;

public sealed record TestOutcome(string Name, string Result);

/// <summary>
/// Reads TRX result files. Both runners can emit them - VSTest with
/// <c>--logger trx</c>, Microsoft.Testing.Platform with <c>--report-trx</c> - which makes TRX the
/// one result format the harness can rely on across all four frameworks.
/// </summary>
/// <remarks>
/// <para>
/// The name a result carries is not the same across frameworks, and the difference is not
/// cosmetic. xUnit writes <c>testName="Ns.Cls.Method"</c>; NUnit writes <c>testName="Method"</c>
/// and puts the class in the test *definition* instead. The harness compares these against the
/// fully qualified names the selection produces, so reading <c>testName</c> alone meant that on
/// any NUnit repository every failing test looked unselected.
/// </para>
/// <para>
/// That is not a cosmetic bug either: it made the correctness gate report catastrophic misses that
/// were entirely its own. On NodaTime it claimed 362 of them. A gate that cries wolf is as useless
/// as one that stays silent, and rather more expensive - the first instinct on seeing that number
/// is to start weakening the engine to satisfy it.
/// </para>
/// </remarks>
public static class TrxParser
{
    public static IReadOnlyList<TestOutcome> Parse(string trxPath)
    {
        var document = XDocument.Load(trxPath);

        // The definition is the only place the class is recorded by every runner.
        var qualified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unitTest in document.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
        {
            var id = unitTest.Attribute("id")?.Value;
            var method = unitTest.Elements().FirstOrDefault(e => e.Name.LocalName == "TestMethod");
            var name = method?.Attribute("name")?.Value;

            if (id is not null && name is not null)
            {
                qualified[id] = Qualify(method!.Attribute("className")?.Value, name);
            }
        }

        var results = new List<TestOutcome>();

        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var outcome = element.Attribute("outcome")?.Value;
            if (outcome is null)
            {
                continue;
            }

            var testId = element.Attribute("testId")?.Value;
            var name = testId is not null && qualified.TryGetValue(testId, out var fromDefinition)
                ? fromDefinition
                : element.Attribute("testName")?.Value;

            if (name is not null)
            {
                results.Add(new TestOutcome(name, outcome));
            }
        }

        return results;
    }

    public static IReadOnlyList<string> FailedTests(string trxPath) =>
    [
        .. Parse(trxPath)
            .Where(r => r.Result.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name),
    ];

    private static string Qualify(string? className, string name) =>
        string.IsNullOrEmpty(className) || name.StartsWith(className + ".", StringComparison.Ordinal)
            ? name
            : className + "." + name;

    /// <summary>
    /// Strips the data-case arguments a parameterised test reports, so
    /// <c>Ns.Cls.Method(value: 3)</c> compares equal to the method the selection names.
    /// </summary>
    /// <remarks>
    /// Every parenthesised group, not just a trailing one: NUnit puts the arguments of a
    /// parameterised *fixture* in the class name, so the qualified name can read
    /// <c>Ns.Cls(3).Method(4)</c> and cutting at the first bracket would throw the method away.
    /// </remarks>
    public static string NormalizeTestName(string reportedName)
    {
        if (!reportedName.Contains('(', StringComparison.Ordinal))
        {
            return reportedName.Trim();
        }

        var builder = new StringBuilder(reportedName.Length);
        var depth = 0;

        foreach (var character in reportedName)
        {
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }
}
