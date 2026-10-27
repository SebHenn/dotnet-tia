using System.Text;
using System.Text.Json;
using Tia.Core.Infrastructure;
using Tia.Core.Reporting;

namespace Tia.Cli;

/// <summary>
/// Untested changes as a GitLab Code Quality report (#128), which a merge request shows in its
/// widget and on the changed lines, as GitHub shows annotations.
/// </summary>
/// <remarks>
/// Always a JSON array, empty when nothing is untested or on a full run, because GitLab treats a
/// missing artifact as an error of the job that should have made it. A change with no file is left
/// out: GitLab needs a path to put it on, and the report still lists it.
/// </remarks>
public static class CodeQuality
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Render(AnalysisReport report, bool failOnUntested)
    {
        ArgumentNullException.ThrowIfNull(report);

        var issues = report.UntestedChanges
            .Where(change => change.File is { Length: > 0 })
            .Select(change => new Dictionary<string, object>
            {
                ["description"] = $"{change.Symbol} changed, and no test reaches it. A change here selects nothing; `dotnet tia explain --symbol` shows the nearest code a test does reach.",
                ["check_name"] = "tia-untested",
                // Stable across runs and commits, so GitLab can tell a new finding from an old one.
                ["fingerprint"] = Hashing.Sha256Hex($"tia-untested\n{change.Project}\n{change.Symbol}", 32),
                ["severity"] = failOnUntested ? "major" : "minor",
                ["location"] = new Dictionary<string, object>
                {
                    ["path"] = change.File!,
                    ["lines"] = new Dictionary<string, object> { ["begin"] = change.Line ?? 1 },
                },
            })
            .ToList();

        return JsonSerializer.Serialize(issues, Options) + "\n";
    }

    public static void Write(string path, AnalysisReport report, bool failOnUntested) =>
        File.WriteAllText(path, Render(report, failOnUntested), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
