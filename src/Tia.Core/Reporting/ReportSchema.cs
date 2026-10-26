using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Tia.Core.Reporting;

/// <summary>
/// The JSON Schema of the report that <c>analyze --json</c> and <c>--report</c>
/// write (#94), as published in <c>docs/report.schema.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exported from the types rather than written by hand, so that it cannot describe a report the
/// code no longer writes. A test holds the published file to this export, and another holds the
/// export to the fields of <see cref="AnalysisReport.SchemaVersion"/> 1, so a field removed
/// without a version bump fails the build instead of a consumer.
/// </para>
/// <para>
/// Objects are closed (<c>additionalProperties: false</c>). The report gains fields between
/// releases without a version bump, so a consumer validating against an older copy of this
/// schema should expect to see fields it does not know; the published copy is closed so that the
/// test notices a field the schema does not describe.
/// </para>
/// </remarks>
public static class ReportSchema
{
    public const string Id = "https://github.com/SebHenn/dotnet-tia/blob/main/docs/report.schema.json";

    public static JsonObject Export()
    {
        var options = new JsonSerializerOptions(AnalysisReport.JsonOptions)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        var exported = options.GetJsonSchemaAsNode(typeof(AnalysisReport), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (_, node) =>
            {
                if (node is JsonObject schema && schema.ContainsKey("properties"))
                {
                    schema["additionalProperties"] = false;
                }

                return node;
            },
        });

        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = Id,
            ["title"] = "dotnet-tia report",
            ["description"] = $"What `dotnet tia analyze --json` and `--report <file>` write, for schemaVersion {AnalysisReport.SchemaVersion}. " +
                              "Fields are added between releases without a version bump; one is removed or changes meaning only with one.",
        };

        foreach (var (name, value) in exported.AsObject().ToList())
        {
            exported.AsObject().Remove(name);
            root[name] = value;
        }

        // Always written, and the one field a consumer is told to check before reading on. Pinned
        // to its value, so a report of another version fails validation against this schema
        // rather than being misread by it.
        root["properties"]!["schemaVersion"] = new JsonObject
        {
            ["type"] = "integer",
            ["const"] = AnalysisReport.SchemaVersion,
        };

        var required = root["required"]?.AsArray() ?? [];
        required.Insert(0, "schemaVersion");
        root["required"] = required;

        return root;
    }

    /// <summary>The schema as published: indented, with a trailing newline.</summary>
    public static string Text() =>
        Export().ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
}
