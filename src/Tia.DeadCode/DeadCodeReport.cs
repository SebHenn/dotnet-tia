using System.Text.Json;
using System.Text.Json.Serialization;
using Tia.Core.Model;

namespace Tia.DeadCode;

/// <summary>One declaration nothing reaches.</summary>
/// <param name="File">Relative to the scanned directory, with forward slashes.</param>
/// <param name="Kind"><c>type</c>, <c>method</c>, <c>property</c>, <c>event</c> or <c>field</c>.</param>
/// <param name="Key">The graph's key for the symbol: stable across runs, and what a baseline would hold.</param>
public sealed record Finding(string File, int Line, string Kind, string Symbol, string Project, string Key);

/// <summary>What a scan concluded, in the shape <c>--json</c> writes.</summary>
/// <param name="Ignored">Findings the config's patterns matched. Counted, so a pattern that grew too wide shows.</param>
public sealed record DeadCodeReport(
    int SchemaVersion,
    int Declarations,
    int Roots,
    int Tests,
    int Ignored,
    IReadOnlyList<Finding> Findings)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,

        // The default escapes `<` and `>` for a document that might be pasted into HTML, and
        // every generic in a symbol name would come out as a six-character unicode escape.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <param name="directory">The scanned directory, which paths are reported and matched relative to.</param>
    public static DeadCodeReport Create(
        string directory,
        ImpactGraph graph,
        ReachabilityResult result,
        DeadCodeConfig config,
        int roots,
        int tests)
    {
        var findings = new List<Finding>();
        var ignored = 0;

        foreach (var node in result.Unreached)
        {
            var file = Path.GetRelativePath(directory, node.FilePath!).Replace('\\', '/');
            if (config.IgnorePaths.Any(pattern => pattern.Matches(file)) ||
                config.IgnoreSymbols.Any(pattern => pattern.Matches(node.DisplayName)))
            {
                ignored++;
                continue;
            }

            findings.Add(new Finding(file, node.Line, node.Kind.ToString().ToLowerInvariant(), node.DisplayName, node.ProjectName, node.Key));
        }

        return new DeadCodeReport(CurrentSchemaVersion, graph.NodeCount, roots, tests, ignored, findings);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
