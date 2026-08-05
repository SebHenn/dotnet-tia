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
public sealed record SuiteRun(
    IReadOnlyList<(string Project, string Test)> Failures,
    IReadOnlyList<string> Unobserved);

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

    public SuiteRun RunAll(
        IReadOnlyList<ProjectSelection> projects,
        DotnetTestMode mode,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<(string, string)>();
        var unobserved = new List<string>();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resultsDirectory = Path.Combine(Path.GetTempPath(), "tia-suite-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(resultsDirectory);

            try
            {
                ProcessRunner.Run(
                    "dotnet",
                    Arguments(project, mode, resultsDirectory),
                    repositoryRoot,
                    cancellationToken: cancellationToken);

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

        return new SuiteRun(failures, unobserved);
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
