using System.Globalization;
using System.Text;
using Tia.Core.Reporting;

namespace Tia.Cli;

/// <summary>
/// Untested changes as annotations on a pull request's diff (#92), through GitHub's workflow
/// commands.
/// </summary>
/// <remarks>
/// <para>
/// The text report, the JSON and the job summary all list untested changes, and a reviewer reads
/// none of them: a reviewer reads the diff. A <c>::warning file=…,line=…::</c> line puts the
/// finding on the changed line itself.
/// </para>
/// <para>
/// GitHub shows ten annotations of one level per step and drops the rest without saying so, so
/// this writes ten and then one notice naming how many more there are. Under
/// <c>--fail-on-untested</c> they are errors, because then they are why the step failed.
/// </para>
/// <para>
/// Only under GitHub Actions. Anywhere else the lines would be noise in a log, and another CI
/// system's parser could read them as something else entirely.
/// </para>
/// </remarks>
public static class Annotations
{
    /// <summary>What GitHub shows of one level in one step.</summary>
    public const int MaxPerLevel = 10;

    public static bool Enabled(IReadOnlyDictionary<string, string?>? environment = null) =>
        string.Equals(
            environment is null ? Environment.GetEnvironmentVariable("GITHUB_ACTIONS") : environment.GetValueOrDefault("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static void Write(AnalysisReport report, bool failOnUntested, TextWriter output, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        if (!Enabled(environment))
        {
            return;
        }

        output.Write(Render(report, failOnUntested));
    }

    public static string Render(AnalysisReport report, bool failOnUntested)
    {
        ArgumentNullException.ThrowIfNull(report);

        var output = new StringBuilder();
        var level = failOnUntested ? "error" : "warning";

        foreach (var change in report.UntestedChanges.Take(MaxPerLevel))
        {
            var properties = new List<string>();
            if (change.File is { Length: > 0 } file)
            {
                properties.Add($"file={Property(file)}");
                if (change.Line is { } line)
                {
                    properties.Add(string.Create(CultureInfo.InvariantCulture, $"line={line}"));
                }
            }

            properties.Add($"title={Property("No test reaches this")}");

            output.Append(CultureInfo.InvariantCulture, $"::{level} {string.Join(',', properties)}::")
                .Append(Data($"{change.Symbol} changed, and no test reaches it. A change here selects nothing; `dotnet tia explain --symbol` shows the nearest code a test does reach."))
                .Append('\n');
        }

        var more = report.UntestedChanges.Count - MaxPerLevel;
        if (more > 0)
        {
            output.Append("::notice title=")
                .Append(Property("More untested changes"))
                .Append("::")
                .Append(Data(string.Create(CultureInfo.InvariantCulture,
                    $"{more} more changed symbol(s) are reached by no test. GitHub shows {MaxPerLevel} annotations of a kind per step; the job summary and --report list them all.")))
                .Append('\n');
        }

        return output.ToString();
    }

    /// <summary>A workflow command's message: <c>%</c>, CR and LF escaped.</summary>
    internal static string Data(string text) =>
        text.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);

    /// <summary>A workflow command's property value: as <see cref="Data"/>, and <c>:</c> and <c>,</c> too.</summary>
    internal static string Property(string text) =>
        Data(text)
            .Replace(":", "%3A", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal);
}
