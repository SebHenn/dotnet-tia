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

/// <summary>
/// Where the analysis time went. Worth reporting rather than profiling for: this tool competes
/// with the suite it is trying to avoid running, so its own cost is a product concern.
/// </summary>
public sealed record PhaseTimings
{
    public double WorkspaceLoadSeconds { get; init; }

    /// <summary>MSBuild evaluating every project. Part of <see cref="WorkspaceLoadSeconds"/>.</summary>
    public double SolutionOpenSeconds { get; init; }

    /// <summary>Roslyn producing compilations. Part of <see cref="WorkspaceLoadSeconds"/>.</summary>
    public double CompilationSeconds { get; init; }

    /// <summary>Running source generators to see what they emit. Part of <see cref="WorkspaceLoadSeconds"/>.</summary>
    public double GeneratorProbeSeconds { get; init; }

    public double CompileCheckSeconds { get; init; }

    public double FingerprintSeconds { get; init; }

    public double GraphSeconds { get; init; }

    public double DiffSeconds { get; init; }

    public double SelectionSeconds { get; init; }
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

    /// <summary>
    /// Tests the graph selected. This is the engine's precision.
    /// </summary>
    public int ImpactedTests { get; init; }

    /// <summary>
    /// Tests that will actually run. Higher than <see cref="ImpactedTests"/> whenever a project
    /// runs unfiltered - because the selection covers most of it, or because the filter would not
    /// fit on a command line - so the two must not be conflated when judging the tool.
    /// </summary>
    public required int SelectedTests { get; init; }

    public IReadOnlyList<ProjectSelection> Projects { get; init; } = [];

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public PhaseTimings Timings { get; init; } = new();

    public double ElapsedSeconds { get; init; }

    public bool IsFullRun => Mode == "full";

    public double SelectionRatio => TotalTests == 0 ? 0 : (double)SelectedTests / TotalTests;

    public double ImpactRatio => TotalTests == 0 ? 0 : (double)ImpactedTests / TotalTests;

    /// <summary>
    /// How long the full suite has to take before selecting is worth it, in seconds, or null when
    /// it never is.
    /// </summary>
    /// <remarks>
    /// Analysis is not free, and a tool that hides its own cost is exactly the kind that gets
    /// abandoned after someone measures it. With analysis cost <c>A</c>, full suite time <c>T</c>
    /// and selected fraction <c>f</c>, running selectively takes <c>A + fT</c>, which beats
    /// <c>T</c> only when <c>T > A / (1 - f)</c>. That number is worth printing: it converts a
    /// selection ratio into the one question an adopter actually has.
    /// </remarks>
    public double? BreakEvenSuiteSeconds =>
        SelectionRatio >= 1 || ElapsedSeconds <= 0 ? null : ElapsedSeconds / (1 - SelectionRatio);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
