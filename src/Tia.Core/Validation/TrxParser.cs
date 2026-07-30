using System.Xml.Linq;

namespace Tia.Core.Validation;

public sealed record TestOutcome(string Name, string Result);

/// <summary>
/// Reads TRX result files. Both runners can emit them - VSTest with
/// <c>--logger trx</c>, Microsoft.Testing.Platform with <c>--report-trx</c> - which makes TRX the
/// one result format the harness can rely on across all four frameworks.
/// </summary>
public static class TrxParser
{
    public static IReadOnlyList<TestOutcome> Parse(string trxPath)
    {
        var document = XDocument.Load(trxPath);
        var results = new List<TestOutcome>();

        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var name = element.Attribute("testName")?.Value;
            var outcome = element.Attribute("outcome")?.Value;
            if (name is not null && outcome is not null)
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

    /// <summary>
    /// Strips the data-case suffix a parameterised test reports, so
    /// <c>Ns.Cls.Method(value: 3)</c> compares equal to the method the selection names.
    /// </summary>
    public static string NormalizeTestName(string reportedName)
    {
        var parenthesis = reportedName.IndexOf('(');
        var name = parenthesis < 0 ? reportedName : reportedName[..parenthesis];
        return name.Trim();
    }
}
