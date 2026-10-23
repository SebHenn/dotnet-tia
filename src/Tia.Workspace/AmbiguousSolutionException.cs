namespace Tia.Workspace;

/// <summary>
/// More than one solution of the same kind sits in the directory being analysed, and nothing says
/// which one was meant.
/// </summary>
/// <remarks>
/// <para>
/// Discovery used to sort the matches and take the first, and the choice showed only in the verbose
/// <c>Loading X...</c> line (issue #37). A selection computed on the wrong solution silently misses
/// every test project that only the other one contains - and nothing downstream can notice, because
/// from inside the analysis the solution it was handed is the whole repository.
/// </para>
/// <para>
/// Deliberately not an <see cref="InvalidOperationException"/>. Several commands catch that type to
/// turn a refusal into exit 1, and <see cref="SolutionAnalyzer"/> catches everything to fall back to
/// a full run. A full run would be the worst answer here: it is computed from the same arbitrarily
/// chosen solution, so it runs the wrong suite whole and reports a pass. This is a question for the
/// caller, and it has to reach the caller as one.
/// </para>
/// </remarks>
public sealed class AmbiguousSolutionException(string directory, string pattern, IReadOnlyList<string> candidates)
    : SetupException(Describe(directory, pattern, candidates))
{
    /// <summary>The file names that matched, in ordinal order.</summary>
    public IReadOnlyList<string> Candidates { get; } = candidates;

    private static string Describe(string directory, string pattern, IReadOnlyList<string> candidates) =>
        $"{candidates.Count} files match {pattern} in '{directory}': {string.Join(", ", candidates)}. " +
        "tia will not pick one, because a selection computed on the wrong solution silently misses every " +
        "test project only the others contain. Pass --solution to name the one to analyse.";
}
