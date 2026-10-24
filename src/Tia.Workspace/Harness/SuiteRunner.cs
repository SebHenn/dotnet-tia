using System.Globalization;
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
/// <param name="OutOfMemory">
/// Projects whose run reached the heap limit. Also a subset of <see cref="Unobserved"/>, and for a
/// stronger reason than <see cref="TimedOut"/>: the run did produce outcomes, and they cannot be
/// believed. An <c>OutOfMemoryException</c> lands on whichever allocation hits the limit first,
/// which under a parallel runner is as likely to be an unrelated test as the runaway one - so the
/// failures it leaves behind are not evidence about what the change broke.
/// </param>
public sealed record SuiteRun(
    IReadOnlyList<(string Project, string Test)> Failures,
    IReadOnlyList<string> Unobserved,
    IReadOnlyList<string> FailedProjects,
    IReadOnlyList<string> TimedOut,
    IReadOnlyList<string> OutOfMemory);

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
    /// <summary>The runtime's own setting, read by every .NET process the run starts.</summary>
    internal const string HeapLimitVariable = "DOTNET_GCHeapHardLimit";

    private readonly Action<string> _log = log ?? (_ => { });

    /// <param name="timeout">
    /// How long one project may run before it is killed, or null to wait indefinitely. Null is right
    /// for the baseline run - there is nothing yet to measure it against, and capping it would turn a
    /// slow repository into a failure - and wrong for every run under a mutation, because mutating a
    /// loop is one of the commonest ways to make one that does not terminate.
    /// </param>
    /// <param name="heapLimitBytes">
    /// The most GC heap any one process of the run may hold, or null for no limit. The same
    /// reasoning as <paramref name="timeout"/>, for the other way a mutation runs away: a loop that
    /// allocates without end exhausts a machine long before it exhausts a time budget.
    /// </param>
    public SuiteRun RunAll(
        IReadOnlyList<ProjectSelection> projects,
        DotnetTestMode mode,
        TimeSpan? timeout = null,
        long? heapLimitBytes = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<(string, string)>();
        var unobserved = new List<string>();
        var failedProjects = new List<string>();
        var timedOut = new List<string>();
        var outOfMemory = new List<string>();
        var environment = HeapLimitEnvironment(heapLimitBytes, Environment.GetEnvironmentVariable);

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
                    environment,
                    cancellationToken);

                // A non-zero exit is the one signal available without TRX. It is recorded even when
                // TRX did arrive, because the two disagreeing - a project that failed but reported
                // no failing test - is itself worth being able to see.
                if (!run.Succeeded)
                {
                    failedProjects.Add(project.Name);
                }

                var reports = Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories).ToList();

                // Only under a limit. Without one, running out of memory is the machine's doing
                // rather than the harness's, and a baseline that did it should fail the preflight
                // as unreadable rather than be explained away as a mutation.
                if (heapLimitBytes is not null &&
                    (RanOutOfMemory(run.StandardOutput + run.StandardError) ||
                     reports.Any(trx => RanOutOfMemory(TrxParser.FailureText(trx)))))
                {
                    _log($"{project.Name} reached its heap limit; its outcomes are not read");
                    unobserved.Add(project.Name);
                    outOfMemory.Add(project.Name);
                    continue;
                }

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

        return new SuiteRun(failures, unobserved, failedProjects, timedOut, outOfMemory);
    }

    /// <summary>
    /// The environment that applies a heap limit to every process of a run, or null when there is
    /// nothing to set.
    /// </summary>
    /// <remarks>
    /// A limit the caller already set is left alone. Whoever exported the variable knows something
    /// about this machine or this suite that a fraction of physical memory does not, and the child
    /// inherits it anyway.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string>? HeapLimitEnvironment(
        long? heapLimitBytes,
        Func<string, string?> inherited)
    {
        if (heapLimitBytes is null || !string.IsNullOrEmpty(inherited(HeapLimitVariable)))
        {
            return null;
        }

        // Hexadecimal is not a style choice: the runtime parses every GC setting as hex, so the
        // decimal digits of 4 GiB would be read as roughly 17 TiB and limit nothing.
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HeapLimitVariable] = "0x" + heapLimitBytes.Value.ToString("X", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Whether a run's output shows that something hit the heap limit.
    /// </summary>
    /// <remarks>
    /// Read from the text, because nothing structured says it. The exit code is the same as for any
    /// failing test, and a host that dies outright leaves no result at all. Both runners print a
    /// failing test's exception, and the runtime prints an unhandled one, so the type name reaches
    /// the console or the TRX either way. From a TRX only what failed is read - see
    /// <see cref="TrxParser.FailureText"/> - because the names are there for passing tests too. A
    /// test that fails for some other reason while mentioning the type would cost one sample, and
    /// never let a miss through as clean.
    /// </remarks>
    internal static bool RanOutOfMemory(string output) =>
        output.Contains("OutOfMemoryException", StringComparison.Ordinal);

    internal static string FormatBytes(long bytes) =>
        FormattableString.Invariant($"{bytes / (1024.0 * 1024 * 1024):0.#} GiB");

    /// <summary>The <c>dotnet test</c> invocation that makes this project write TRX.</summary>
    internal static IReadOnlyList<string> Arguments(ProjectSelection project, DotnetTestMode mode, string resultsDirectory)
    {
        var arguments = new List<string> { "test" };

        if (mode == DotnetTestMode.MicrosoftTestingPlatform)
        {
            arguments.Add("--project");
        }

        arguments.Add(project.ProjectPath);

        // Always, unlike `run`: one framework per invocation is what lets a failure be read under
        // the instance that produced it. Without it every instance of a multi-targeted project ran
        // every framework, and reported all of their failures as its own (#67).
        if (project.TargetFramework is { } framework)
        {
            arguments.AddRange(["--framework", framework]);
        }

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
