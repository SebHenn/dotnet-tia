namespace Tia.Cli;

/// <summary>Exit codes the commands share, so the same answer never gets two numbers.</summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>
    /// A defect the command exists to find: a miss for <c>verify</c> and <c>shadow</c>, a failing
    /// test for <c>run</c>.
    /// </summary>
    public const int Failed = 1;

    /// <summary>
    /// Nothing could be concluded. <c>shadow</c> has always used this for an inconclusive verdict,
    /// and a workspace that resolved no tests is the same statement made earlier: `analyze` and
    /// `graph` used to report that state as a result and exit 0, which made every command
    /// downstream plausible and meaningless. <c>verify</c> answers with it when it refuses to start
    /// or has no usable sample; it used to exit 1 for those, the same number as a miss (issue #36).
    /// </summary>
    public const int Inconclusive = 2;

    /// <summary>
    /// <c>run --budget</c> stopped before it had run everything it selected, and what it did run
    /// passed. Its own number because neither 0 nor 1 is true: 0 would report a pass for a
    /// selection that was not finished, and 1 would report a failure nothing showed.
    /// </summary>
    public const int Deferred = 3;
}
