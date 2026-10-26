using Tia.Frameworks;

namespace Tia.Workspace;

public sealed record AnalysisOptions
{
    public required string RepositoryRoot { get; init; }

    public required string BaseRef { get; init; }

    /// <summary>
    /// What to do about <see cref="BaseRef"/> when it does not resolve, appended to that full-run
    /// reason. Set when the base was defaulted rather than given and no default existed (#126).
    /// </summary>
    public string? BaseRefHint { get; init; }

    /// <summary>Solution or project to analyse. Discovered from the repository root when null.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>
    /// Turn any unhandled failure into a full run instead of an error. Defaults to true, because
    /// the cost of a full run is minutes and the cost of a missed test is a broken main branch.
    /// </summary>
    public bool FallbackFullOnError { get; init; } = true;

    public bool UseCache { get; init; } = true;

    /// <summary>
    /// Whether a run that reads the cache may also write it. False for the mutation harness's
    /// per-sample analyses (#39): each one describes a tree with a mutation in it, and saving that
    /// made the next sample rebuild the previous sample's project, made a surface-changing mutation
    /// rebuild every dependent, and left <c>.tia</c> describing a mutation once <c>verify</c> ended.
    /// </summary>
    public bool WriteCache { get; init; } = true;

    public string CacheDirectory { get; init; } = ".tia";

    /// <summary><see cref="CacheDirectory"/> as a full path, relative ones taken from the repository root.</summary>
    public string ResolvedCacheDirectory => Path.IsPathRooted(CacheDirectory)
        ? CacheDirectory
        : Path.Combine(RepositoryRoot, CacheDirectory);

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

    /// <summary>
    /// Check every changed symbol for a test that reaches it, with no cap on the traversals that
    /// costs (#70). Set by <c>--fail-on-untested</c>: a gate that stopped checking partway would
    /// pass whatever it never looked at.
    /// </summary>
    public bool CheckEveryChange { get; init; }

    /// <summary>List the tests a selective run leaves out, per project, in the report (#75).</summary>
    public bool ReportSkipped { get; init; }

    /// <summary>
    /// Tests that failed the last time they ran, put first in their project's order and offered as
    /// its first wave (#72). Changes the order only: a test here that the change does not reach is
    /// not added.
    /// </summary>
    public IReadOnlySet<string> LastFailed { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public double CoverageThreshold { get; init; } = FilterPlanner.DefaultCoverageThreshold;

    public Action<string>? Log { get; init; }
}
