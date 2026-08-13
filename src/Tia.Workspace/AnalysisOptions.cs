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

    /// <summary>
    /// Bound each upward hop by which concrete types a member can obtain, rather than by which it
    /// can reach. Off by default, and deliberately so for at least one release: it is the change
    /// most able to introduce a miss, and the earlier attempt at bounding this walk had to be
    /// reverted. What it costs and what it buys is measured in <c>docs/benchmarks.md</c>.
    /// </summary>
    public bool TypeFlow { get; init; }

    public int? MaxFilterLength { get; init; }

    public double CoverageThreshold { get; init; } = FilterPlanner.DefaultCoverageThreshold;

    public Action<string>? Log { get; init; }
}
