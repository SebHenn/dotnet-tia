using Tia.Core.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tia.DeadCode;

/// <summary>The findings a repository has accepted, so a later run fails only on new ones.</summary>
/// <remarks>
/// Held by symbol key, not by file and line: a line number moves with every edit above it and
/// would turn an accepted finding into a new one. A baseline only ever drops findings the team
/// has already seen, and it says how many it dropped so a stale one shows.
/// </remarks>
public sealed record Baseline(int SchemaVersion, IReadOnlyList<string> Keys)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Baseline From(DeadCodeReport report) =>
        new(CurrentSchemaVersion, [.. report.Findings.Select(finding => finding.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <exception cref="ConfigFileException">Not a baseline this version reads.</exception>
    public static Baseline Parse(string json, string path)
    {
        Baseline? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<Baseline>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigFileException($"{path} is not a baseline: {ex.Message}");
        }

        if (baseline is null || baseline.Keys is null || baseline.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ConfigFileException($"{path} is not a baseline this version reads (expected schemaVersion {CurrentSchemaVersion}).");
        }

        return baseline;
    }

    /// <summary>The report without the findings the baseline holds, and how many that was.</summary>
    public (DeadCodeReport Report, int Accepted) Apply(DeadCodeReport report)
    {
        var known = new HashSet<string>(Keys, StringComparer.Ordinal);
        var fresh = report.Findings.Where(finding => !known.Contains(finding.Key)).ToList();
        return (report with { Findings = fresh }, report.Findings.Count - fresh.Count);
    }
}
