using Tia.Core.Infrastructure;

namespace Tia.Cli.Commands;

/// <summary>
/// Builds the solution while the analysis is still running, so that `dotnet test` does not have to.
/// </summary>
/// <remarks>
/// <para>
/// `tia run` costs the analysis and then a `dotnet test` that starts by building. The two do not
/// depend on each other, so one of them is free. Measured on this repository, one-line edit, warm:
/// analysis 4.59 s and build 2.16 s run to 6.75 s in sequence and **5.25 s** together - the 0.66 s
/// difference from the ideal being what the two cost each other for the machine. The saving is
/// bounded by whichever of the two is smaller, so it is worth most exactly where this tool is
/// worth least: a repository whose suite is short enough that the build is most of it.
/// </para>
/// <para>
/// The hazard is not the concurrency. MSBuild's evaluation reads `obj/` while the build rewrites
/// it, which was measured three times over and never once disturbed the analysis - and if it ever
/// does, the failure lands in the fall-back-to-a-full-run path, which is slow rather than wrong.
/// </para>
/// <para>
/// The hazard is `--no-build`. Passing it when the build that ran was not the build `dotnet test`
/// would have run means executing yesterday's binaries and reporting them as today's, which is the
/// one class of wrong answer this tool exists to avoid. So it is passed only when this class ran
/// the build itself, over the same solution the analysis loaded, with no arguments from the caller
/// that could have changed what "the build" means. Anything after `--` disables it outright rather
/// than being reasoned about.
/// </para>
/// </remarks>
public static class BuildAhead
{
    /// <summary>
    /// Whether a build started now would be the same build <c>dotnet test</c> would run.
    /// </summary>
    /// <param name="passthrough">
    /// Whatever the caller wrote after <c>--</c>. Any of it is disqualifying: `--configuration
    /// Release` alone means the build this would run is the wrong one, and there is no list of
    /// arguments that are known to be harmless worth maintaining against `dotnet test`'s.
    /// </param>
    public static bool Applies(bool dryRun, IReadOnlyList<string> passthrough) =>
        !dryRun && passthrough.Count == 0;

    /// <summary>
    /// Starts the build and returns immediately. The caller must await the result before invoking
    /// anything that depends on it - and must await it even when it has decided not to run tests,
    /// because a build left running is a process nobody is going to stop.
    /// </summary>
    public static Task<ProcessResult> Start(string? target, string repositoryRoot, CancellationToken cancellationToken)
    {
        // The solution the analysis loaded, so that every project it can name is one this build
        // covered. Falling back to the working directory matches what `dotnet build` does alone.
        string[] arguments = target is { Length: > 0 }
            ? ["build", target]
            : ["build"];

        return Task.Run(
            () => ProcessRunner.Run("dotnet", arguments, repositoryRoot, cancellationToken: cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Reports a failed build as a failed build. Returns the exit code to stop with, or null when
    /// the build succeeded and `--no-build` may be passed.
    /// </summary>
    /// <remarks>
    /// Worth being explicit about: without this, a solution that does not compile would surface as
    /// whatever the analysis made of it - most likely a full run, since a project that does not
    /// bind forces one - and the reader would be told about test selection when the actual news is
    /// that the build is broken.
    /// </remarks>
    public static int? Report(ProcessResult build)
    {
        if (build.Succeeded)
        {
            return null;
        }

        Console.Error.WriteLine("  The build failed, so no tests were run.");
        Console.Error.WriteLine();

        foreach (var line in build.StandardOutput.Split('\n').Concat(build.StandardError.Split('\n')))
        {
            if (line.Trim().Length > 0)
            {
                Console.Error.WriteLine("  " + line.TrimEnd());
            }
        }

        Console.Error.WriteLine();
        return build.ExitCode;
    }
}
