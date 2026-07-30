using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tia.Core.Reporting;

public sealed record WideningEvent(string Cause, string Scope, string Detail);

public sealed record DiffSummary
{
    public required int FileCount { get; init; }

    public required int CSharpFileCount { get; init; }

    public required int ChangedSymbolCount { get; init; }

    public IReadOnlyList<string> Files { get; init; } = [];
}

public sealed record GraphSummary
{
    public required int Types { get; init; }

    public required int Members { get; init; }

    public required int Edges { get; init; }

    public required bool FromCache { get; init; }

    public required int ProjectsRebuilt { get; init; }

    public required int ProjectsReused { get; init; }
}

public sealed record ProjectSelection
{
    public required string Name { get; init; }

    public required string ProjectPath { get; init; }

    public string? AssemblyPath { get; init; }

    public required string Framework { get; init; }

    public required string Runner { get; init; }

    public required int TotalTests { get; init; }

    public required int SelectedTests { get; init; }

    /// <summary>False when the project runs unfiltered - either because nearly everything was
    /// selected, or because the filter would exceed the platform command-line limit.</summary>
    public required bool Filtered { get; init; }

    public string? UnfilteredReason { get; init; }

    /// <summary>Arguments to append to <c>dotnet test &lt;project&gt;</c>, dialect-specific.</summary>
    public IReadOnlyList<string> FilterArguments { get; init; } = [];

    public IReadOnlyList<string> Tests { get; init; } = [];
}

/// <summary>The complete result of an analysis, and the shape of <c>--json</c>.</summary>
public sealed record AnalysisReport
{
    /// <summary><c>selective</c> or <c>full</c>.</summary>
    public required string Mode { get; init; }

    public required string BaseRef { get; init; }

    public string? BaseCommit { get; init; }

    public string? HeadCommit { get; init; }

    /// <summary>
    /// Which <c>dotnet test</c> command this repository gets - <c>VsTest</c> or
    /// <c>MicrosoftTestingPlatform</c>. Repository-wide, set by <c>global.json</c>, and separate
    /// from any project's runner: the two commands take the project and the filter in different
    /// places.
    /// </summary>
    public string DotnetTestMode { get; init; } = "VsTest";

    /// <summary>Why the whole suite has to run. Empty in selective mode.</summary>
    public IReadOnlyList<string> FullRunReasons { get; init; } = [];

    /// <summary>Every scope expansion applied, so conservatism is never silent.</summary>
    public IReadOnlyList<WideningEvent> Widenings { get; init; } = [];

    public DiffSummary Diff { get; init; } = new() { FileCount = 0, CSharpFileCount = 0, ChangedSymbolCount = 0 };

    public GraphSummary Graph { get; init; } = new()
    {
        Types = 0, Members = 0, Edges = 0, FromCache = false, ProjectsRebuilt = 0, ProjectsReused = 0,
    };

    public required int TotalTests { get; init; }

    public required int SelectedTests { get; init; }

    public IReadOnlyList<ProjectSelection> Projects { get; init; } = [];

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public double ElapsedSeconds { get; init; }

    public bool IsFullRun => Mode == "full";

    public double SelectionRatio => TotalTests == 0 ? 0 : (double)SelectedTests / TotalTests;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
