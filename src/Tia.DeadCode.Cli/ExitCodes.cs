namespace Tia.DeadCode.Cli;

/// <summary>Exit codes the commands share, so the same answer never gets two numbers.</summary>
public static class ExitCodes
{
    /// <summary>The solution was analysed in full and nothing unused was found.</summary>
    public const int Clean = 0;

    /// <summary>Unused code, references or files were found.</summary>
    public const int Findings = 1;

    /// <summary>
    /// Nothing could be concluded: the solution did not load, or loaded in part. Never 0 for
    /// these, because a project that failed to load contributes no references, and everything
    /// only it used would be reported as dead - or, worse, a scan that saw nothing would pass.
    /// The same number and the same meaning as dotnet-tia's <c>Inconclusive</c>.
    /// </summary>
    public const int Inconclusive = 2;
}
