using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tia.DeadCode;

/// <summary>A scan as SARIF 2.1.0, which GitHub code scanning shows as annotations.</summary>
public static class Sarif
{
    public const string RuleId = "DEAD001";

    public static string Write(DeadCodeReport report)
    {
        var results = new JsonArray();
        foreach (var finding in report.Findings)
        {
            results.Add(new JsonObject
            {
                ["ruleId"] = RuleId,
                ["level"] = "warning",
                ["message"] = new JsonObject { ["text"] = $"{finding.Kind} {finding.Symbol} is not reached from an entry point or a test." },
                ["locations"] = new JsonArray(new JsonObject
                {
                    ["physicalLocation"] = new JsonObject
                    {
                        ["artifactLocation"] = new JsonObject { ["uri"] = finding.File },
                        ["region"] = new JsonObject { ["startLine"] = Math.Max(finding.Line, 1) },
                    },
                }),
                ["partialFingerprints"] = new JsonObject { ["symbolKey/v1"] = finding.Key },
            });
        }

        var log = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = "dotnet-deadcode",
                        ["rules"] = new JsonArray(new JsonObject
                        {
                            ["id"] = RuleId,
                            ["shortDescription"] = new JsonObject { ["text"] = "Declaration nothing reaches" },
                        }),
                    },
                },
                ["results"] = results,
            }),
        };

        return log.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
