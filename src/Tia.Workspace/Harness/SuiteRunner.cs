using Tia.Core.Infrastructure;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Validation;
using Tia.Frameworks;

namespace Tia.Workspace.Harness;

/// <summary>
/// One full-suite run, and what could be read from it.
/// </summary>
/// <param name="Unobserved">
/// Projects whose outcome could not be read. Distinct from "nothing failed", and the distinction is
/// the whole point: a harness that reports a clean result from a run it could not observe is worse
/// than no harness.
/// </param>
/// <param name="FailedProjects">
/// Projects whose <c>dotnet test</c> exited non-zero. Strictly weaker than <see cref="Failures"/>:
/// it says a project has *a* failing test without saying which, so it can prove a miss - a project
/// that failed, none of whose tests were selected, was definitely missed - and can never prove the
/// absence of one. Populated always; only the project-granularity gate reads it.
/// </param>
/// <param name="TimedOut">
/// Projects killed for exceeding the budget. A subset of <see cref="Unobserved"/> - a suite that was
/// killed produced no outcome to read, and everything downstream should treat it that way - but kept
/// separately because the two need different words. "No TRX was produced" tells the reader to add a
/// package; it is the wrong instruction entirely for a suite that ran and would not stop.
/// </param>
public sealed record SuiteRun(
    IReadOnlyList<(string Project, string Test)> Failures,
    IReadOnlyList<string> Unobserved,
    IReadOnlyList<string> FailedProjects,
    IReadOnlyList<string> TimedOut);

/// <summary>
/// Runs whole test projects and reads their outcomes from TRX.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the two harnesses that need ground truth about what actually failed: the mutation gate,
/// which injects a fault and asks whether the selection covered it, and shadow mode, which asks the
/// same question of a real diff. They differ in where the change comes from, not in how a suite is
/// run or how its results are read.
/// </para>
/// <para>
/// TRX is the one result format both runners emit - VSTest through <c>--logger trx</c>,
/// Microsoft.Testing.Platform through <c>--report-trx</c> - which is what makes a single
/// implementation work across all four frameworks. Which of the three argument shapes applies
/// depends on the project's own runner *and* on whether the repository opted into the
/// platform-native <c>dotnet test</c>, because that moves every project onto <c>--project</c> and
/// drops the <c>--</c> separator.
/// </para>
/// </remarks>
public sealed class SuiteRunner(string repositoryRoot, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <param name="timeout">
    /// How long one project may run before it is killed, or null to wait indefinitely. Null is right
    /// for the baseline run - there is nothing yet to measure it against, and capping it would turn a
    /// slow repository into a failure - and wrong for every run under a mutation, because mutating a
    /// loop is one of the commonest ways to make one that does not terminate.
    /// </param>
    public SuiteRun RunAll(
        IReadOnlyList<ProjectSelection> projects,
        DotnetTestMode mode,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<(string, string)>();
        var unobserved = new List<string>();
        var failedProjects = new List<string>();
        var timedOut = new List<string>();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resultsDirectory = Path.Combine(Path.GetTempPath(), "tia-suite-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(resultsDirectory);

            try
            {
                var run = ProcessRunner.Run(
                    "dotnet",
                    Arguments(project, mode, resultsDirectory),
                    repositoryRoot,
                    timeout,
                    cancellationToken: cancellationToken);

                // A non-zero exit is the one signal available without TRX. It is recorded even when
                // TRX did arrive, because the two disagreeing - a project that failed but reported
                // no failing test - is itself worth being able to see.
                if (!run.Succeeded)
                {
                    failedProjects.Add(project.Name);
                }

                var reports = Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories).ToList();
                if (reports.Count == 0)
                {
                    unobserved.Add(project.Name);
                    continue;
                }

                foreach (var trx in reports)
                {
                    foreach (var failed in TrxParser.FailedTests(trx))
                    {
                        failures.Add((project.Name, failed));
                    }
                }
            }
            catch (TimeoutException ex)
            {
                // Both lists on purpose. Unobserved is what stops anything downstream calling this
                // sample clean; TimedOut is what lets it say why in the right words.
                _log($"{project.Name} was killed for running past its budget: {ex.Message}");
                unobserved.Add(project.Name);
                timedOut.Add(project.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"could not collect results for {project.Name}: {ex.Message}");
                unobserved.Add(project.Name);
            }
            finally
            {
                TryDelete(resultsDirectory);
            }
        }

        return new SuiteRun(failures, unobserved, failedProjects, timedOut);
    }

    /// <summary>The <c>dotnet test</c> invocation that makes this project write TRX.</summary>
    internal static IReadOnlyList<string> Arguments(ProjectSelection project, DotnetTestMode mode, string resultsDirectory)
    {
        var arguments = new List<string> { "test" };

        if (mode == DotnetTestMode.MicrosoftTestingPlatform)
        {
            arguments.Add("--project");
        }

        arguments.Add(project.ProjectPath);

        var onTestingPlatform = Enum.TryParse<TestRunner>(project.Runner, out var runner)
                                && runner == TestRunner.MicrosoftTestingPlatform;

        if (mode == DotnetTestMode.MicrosoftTestingPlatform)
        {
            arguments.AddRange(["--report-trx", "--results-directory", resultsDirectory]);
        }
        else if (onTestingPlatform)
        {
            arguments.AddRange(["--", "--report-trx", "--results-directory", resultsDirectory]);
        }
        else
        {
            arguments.AddRange(["--logger", "trx", "--results-directory", resultsDirectory]);
        }

        return arguments;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a run over.
        }
    }
}
