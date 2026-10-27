using System.Globalization;
using System.Text;
using Tia.Core.Reporting;

namespace Tia.Cli;

/// <summary>
/// Untested changes as annotations on a pull request's diff (#92), through GitHub's workflow
/// commands, or Azure Pipelines' logging commands (#128).
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
/// Only under a CI system that reads them: GitHub Actions, or Azure Pipelines, whose
/// <c>##vso[task.logissue]</c> lands on the build summary and the pull request. Anywhere else the
/// lines would be noise in a log, and another CI system's parser could read them as something else
/// entirely.
/// </para>
/// </remarks>
public static class Annotations
{
    /// <summary>What GitHub shows of one level in one step. Azure Pipelines is held to it too.</summary>
    public const int MaxPerLevel = 10;

    /// <summary>The CI system a run is in, as far as its own commands go.</summary>
    public enum CiSystem
    {
        None,
        GitHubActions,
        AzurePipelines,
    }

    public static CiSystem Detect(IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? Read(string name) => environment is null ? Environment.GetEnvironmentVariable(name) : environment.GetValueOrDefault(name);

        if (string.Equals(Read("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return CiSystem.GitHubActions;
        }

        return string.Equals(Read("TF_BUILD"), "true", StringComparison.OrdinalIgnoreCase) ? CiSystem.AzurePipelines : CiSystem.None;
    }

    public static bool Enabled(IReadOnlyDictionary<string, string?>? environment = null) => Detect(environment) != CiSystem.None;

    public static void Write(AnalysisReport report, bool failOnUntested, TextWriter output, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        switch (Detect(environment))
        {
            case CiSystem.GitHubActions:
                output.Write(Render(report, failOnUntested));
                break;
            case CiSystem.AzurePipelines:
                output.Write(RenderAzure(report, failOnUntested));
                break;
        }
    }

    /// <summary>
    /// Under Azure Pipelines, attaches a Markdown summary to the build's summary page (#128), which
    /// GitHub reads from <c>$GITHUB_STEP_SUMMARY</c> without being told. Nothing elsewhere.
    /// </summary>
    public static void AttachSummary(string path, TextWriter output, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (Detect(environment) == CiSystem.AzurePipelines)
        {
            output.Write($"##vso[task.uploadsummary]{AzureData(Path.GetFullPath(path))}\n");
        }
    }

    /// <summary>As <see cref="Render"/>, in Azure Pipelines' <c>##vso[task.logissue]</c> commands.</summary>
    public static string RenderAzure(AnalysisReport report, bool failOnUntested)
    {
        ArgumentNullException.ThrowIfNull(report);

        var output = new StringBuilder();
        var level = failOnUntested ? "error" : "warning";

        foreach (var change in report.UntestedChanges.Take(MaxPerLevel))
        {
            output.Append(CultureInfo.InvariantCulture, $"##vso[task.logissue type={level};");
            if (change.File is { Length: > 0 } file)
            {
                output.Append(CultureInfo.InvariantCulture, $"sourcepath={AzureProperty(file)};");
                if (change.Line is { } line)
                {
                    output.Append(CultureInfo.InvariantCulture, $"linenumber={line};");
                }
            }

            output.Append("code=tia-untested;]")
                .Append(AzureData($"{change.Symbol} changed, and no test reaches it. A change here selects nothing; `dotnet tia explain --symbol` shows the nearest code a test does reach."))
                .Append('\n');
        }

        var more = report.UntestedChanges.Count - MaxPerLevel;
        if (more > 0)
        {
            output.Append("##vso[task.logissue type=warning;code=tia-untested;]")
                .Append(AzureData(string.Create(CultureInfo.InvariantCulture,
                    $"{more} more changed symbol(s) are reached by no test. The summary and --report list them all.")))
                .Append('\n');
        }

        return output.ToString();
    }

    /// <summary>A logging command's message, escaped as azure-pipelines-task-lib escapes it.</summary>
    internal static string AzureData(string text) =>
        text.Replace("%", "%AZP25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);

    /// <summary>A logging command's property value: as <see cref="AzureData"/>, and <c>]</c> and <c>;</c> too.</summary>
    internal static string AzureProperty(string text) =>
        AzureData(text)
            .Replace("]", "%5D", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal);

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
