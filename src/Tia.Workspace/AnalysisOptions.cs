using Tia.Frameworks;

namespace Tia.Workspace;

public sealed record AnalysisOptions
{
    public required string RepositoryRoot { get; init; }

    public required string BaseRef { get; init; }

    /// <summary>Solution or project to analyse. Discovered from the repository root when null.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>
    /// Turn any unhandled failure into a full run instead of an error. Defaults to true, because
    /// the cost of a full run is minutes and the cost of a missed test is a broken main branch.
    /// </summary>
    public bool FallbackFullOnError { get; init; } = true;

    public bool UseCache { get; init; } = true;

    public string CacheDirectory { get; init; } = ".tia";

    public bool ForceFull { get; init; }

    /// <summary>
    /// Selection is a pull-request optimisation. On the default branch the suite always runs whole,
    /// so the mainline stays honest about what passes.
    /// </summary>
    public string? DefaultBranch { get; init; }

    public int? MaxFilterLength { get; init; }

    public double CoverageThreshold { get; init; } = FilterPlanner.DefaultCoverageThreshold;

    public Action<string>? Log { get; init; }
}
