using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Tia.Core.Reporting;

namespace Tia.Core.Tests;

/// <summary>
/// The published JSON Schema of the report (#94), held to what the code writes.
/// </summary>
/// <remarks>
/// The report is read by other programs - ci-failure-intelligence's <c>selection-misses</c>, and
/// anyone's script over <c>--json</c> - and its shape was documented by an example and enforced by
/// nothing. Four checks close that: the published file is the export, every field of schema
/// version 1 is still exported, real reports validate, and the sample reports exercise every field
/// the schema describes, so a field added in C# has to be added here too.
/// </remarks>
public sealed class ReportSchemaTests
{
    private const string Regenerate =
        "Regenerate it with: TIA_WRITE_SCHEMA=1 dotnet tests/Tia.Core.Tests/bin/Release/net10.0/Tia.Core.Tests.dll -class \"*ReportSchemaTests\"";

    private static readonly string Root = FindRoot();

    /// <summary>Parsed once: the library registers a schema under its <c>$id</c>, and refuses a second.</summary>
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(ReportSchema.Text()));

    private static string PublishedPath => Path.Combine(Root, "docs", "report.schema.json");

    private static string FrozenPath(int version) => Path.Combine(Root, "tests", "Tia.Core.Tests", $"report-schema-v{version}.txt");

    [Fact]
    public void The_published_schema_is_the_one_the_types_export()
    {
        var exported = ReportSchema.Text();
        if (Environment.GetEnvironmentVariable("TIA_WRITE_SCHEMA") == "1")
        {
            File.WriteAllText(PublishedPath, exported);
        }

        var published = File.Exists(PublishedPath) ? File.ReadAllText(PublishedPath).ReplaceLineEndings("\n") : string.Empty;
        Assert.True(published == exported, $"docs/report.schema.json does not match the report types. {Regenerate}");
    }

    [Fact]
    public void Every_field_of_this_schema_version_is_still_exported()
    {
        // Adding a field keeps the version; removing or renaming one is a breaking change, and the
        // version says so. The list is frozen when a version is first published.
        var path = FrozenPath(AnalysisReport.SchemaVersion);
        var current = SchemaPaths(ReportSchema.Export());

        if (Environment.GetEnvironmentVariable("TIA_WRITE_SCHEMA") == "1" && !File.Exists(path))
        {
            File.WriteAllLines(path, current.Order(StringComparer.Ordinal));
        }

        Assert.True(File.Exists(path), $"No frozen field list for schemaVersion {AnalysisReport.SchemaVersion}. {Regenerate}");

        var missing = File.ReadAllLines(path).Where(l => l.Length > 0).Except(current, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"Fields of schemaVersion {AnalysisReport.SchemaVersion} are gone: {string.Join(", ", missing)}. " +
            "Removing or renaming a field needs AnalysisReport.SchemaVersion bumped, and a changelog entry saying so.");
    }

    [Fact]
    public void Reports_of_every_shape_validate()
    {
        var schema = Schema.Value;

        foreach (var (name, report) in Samples())
        {
            var result = schema.Evaluate(JsonDocument.Parse(report.ToJson()).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, $"{name}: {Errors(result)}");
        }
    }

    [Fact]
    public void A_report_with_a_field_the_schema_does_not_describe_does_not_validate()
    {
        var schema = Schema.Value;
        var json = JsonNode.Parse(Samples().First().Report.ToJson())!.AsObject();
        json["projects"]![0]!.AsObject()["somethingNew"] = 1;

        Assert.False(schema.Evaluate(JsonDocument.Parse(json.ToJsonString()).RootElement).IsValid);
    }

    [Fact]
    public void The_samples_exercise_every_field_the_schema_describes()
    {
        var described = SchemaPaths(ReportSchema.Export());
        var written = Samples().SelectMany(s => InstancePaths(JsonNode.Parse(s.Report.ToJson()))).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(described.Except(written, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    private static IEnumerable<(string Name, AnalysisReport Report)> Samples()
    {
        var project = new ProjectSelection
        {
            Name = "App.Tests",
            ProjectPath = "/repo/App.Tests/App.Tests.csproj",
            AssemblyPath = "/repo/App.Tests/bin/App.Tests.dll",
            Framework = "XUnitV3",
            Runner = "MicrosoftTestingPlatform",
            TargetFramework = "net10.0",
            WritesTrx = true,
            HasRunSettings = false,
            TotalTests = 10,
            SelectedTests = 3,
            Filtered = true,
            FilterArguments = ["--filter-class", "App.Tests.A"],
            FilterInFile = true,
            FirstWave = new RunWave
            {
                TestCount = 1,
                FilterArguments = ["--filter-method", "App.Tests.A.One"],
                RemainderFilterArguments = ["--filter-method", "App.Tests.A.Two"],
                LastFailed = true,
            },
            Tests = ["App.Tests.A.One", "App.Tests.A.Two", "App.Tests.A.Three"],
        };

        var selective = new AnalysisReport
        {
            Mode = "selective",
            BaseRef = "origin/main",
            BaseCommit = "73be09d0",
            HeadCommit = "a518537",
            MSBuild = "17.14 (/usr/share/dotnet/sdk/10.0.100)",
            Solution = "App.slnx",
            Widenings = [new WideningEvent("Reflection", "App", "Activator.CreateInstance")],
            Diff = new DiffSummary { FileCount = 2, CSharpFileCount = 1, ChangedSymbolCount = 3, Files = ["src/App/A.cs", "data.json"] },
            Graph = new GraphSummary { Types = 10, Members = 50, Edges = 200, FromCache = true, ProjectsRebuilt = 1, ProjectsReused = 2 },
            TotalTests = 10,
            ImpactedTests = 2,
            SelectedTests = 3,
            Projects =
            [
                project,
                project with { Name = "Other.Tests", Filtered = false, UnfilteredReason = "selection covers 90%", FilterInFile = false, FirstWave = null, UnsplitReason = "nothing to divide" },
            ],
            Shard = new ShardInfo { Index = 1, Count = 2, SelectedAcrossShards = 6, Weighting = "durations", DurationsFingerprint = "0a1b2c3d4e5f", EstimatedSeconds = 12.5 },
            SkippedTests = [new SkippedTests { Project = "App.Tests", Tests = ["App.Tests.B.Four"] }],
            UnanalysedTestProjects = [new UnanalysedTestProject { Name = "F.Tests", Language = "F#", ProjectPath = "/repo/F.Tests.fsproj", Runs = true, Reason = "a project it depends on changed" }],
            Diagnostics = ["CS0246 in src/App/A.cs"],
            UntestedChanges = [new UntestedChange { Symbol = "A.Refund()", Project = "App", File = "src/App/A.cs" }],
            UncheckedChanges = 1,
            ElapsedSeconds = 4.2,
        };

        yield return ("selective, every optional field", selective);

        yield return ("full run", new AnalysisReport
        {
            Mode = "full",
            BaseRef = "origin/main",
            FullRunReasons = ["global.json changed"],
            TotalTests = 10,
            SelectedTests = 10,
            Projects = [new ProjectSelection { Name = "App.Tests", ProjectPath = "/repo/App.Tests.csproj", Framework = "NUnit", Runner = "VsTest", TotalTests = 10, SelectedTests = 10, Filtered = false, UnfilteredReason = "full run" }],
        });

        yield return ("unusable workspace", new AnalysisReport
        {
            Mode = "full",
            BaseRef = "origin/main",
            LoadFailures = ["App.csproj: the SDK did not resolve"],
            FullRunReasons = ["App.csproj: the SDK did not resolve"],
            TotalTests = 0,
            SelectedTests = 0,
        });
    }

    /// <summary>Every property the schema describes, as a dotted path with <c>[]</c> for array items.</summary>
    private static HashSet<string> SchemaPaths(JsonObject root)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        Walk(root, string.Empty, 0);
        return paths;

        void Walk(JsonNode? node, string prefix, int depth)
        {
            if (node is not JsonObject schema || depth > 32)
            {
                return;
            }

            if (schema["$ref"]?.GetValue<string>() is { } reference)
            {
                Walk(Resolve(root, reference), prefix, depth + 1);
                return;
            }

            if (schema["properties"] is JsonObject properties)
            {
                foreach (var (name, property) in properties)
                {
                    var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
                    paths.Add(path);
                    Walk(property, path, depth + 1);
                }
            }

            if (schema["items"] is { } items)
            {
                Walk(items, prefix + "[]", depth + 1);
            }

            foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
            {
                if (schema[keyword] is JsonArray alternatives)
                {
                    foreach (var alternative in alternatives)
                    {
                        Walk(alternative, prefix, depth + 1);
                    }
                }
            }
        }
    }

    private static JsonNode? Resolve(JsonObject root, string reference)
    {
        if (reference == "#")
        {
            return root;
        }

        JsonNode? node = root;
        foreach (var segment in reference.TrimStart('#').Trim('/').Split('/'))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject o => o[key],
                JsonArray a when int.TryParse(key, out var i) => a[i],
                _ => null,
            };
        }

        return node;
    }

    private static IEnumerable<string> InstancePaths(JsonNode? node, string prefix = "")
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o)
                {
                    var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
                    yield return path;
                    foreach (var inner in InstancePaths(value, path))
                    {
                        yield return inner;
                    }
                }

                break;

            case JsonArray a:
                foreach (var item in a)
                {
                    foreach (var inner in InstancePaths(item, prefix + "[]"))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    private static string Errors(EvaluationResults result) =>
        string.Join("; ", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Key}: {e.Value}")));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tia.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("tia.slnx not found above the test assembly.");
    }
}
