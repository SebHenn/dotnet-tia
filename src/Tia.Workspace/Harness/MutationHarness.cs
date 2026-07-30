using Tia.Core.Infrastructure;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Validation;
using Tia.Frameworks;

namespace Tia.Workspace.Harness;

public enum SampleOutcome
{
    /// <summary>Every failing test was selected.</summary>
    Clean,

    /// <summary>A failing test was not selected. The only fatal defect class this tool has.</summary>
    Miss,

    /// <summary>Nothing to check: no mutation site, or the analysis bailed out to a full run.</summary>
    Skipped,
}

public sealed record MutationSample
{
    public required int Index { get; init; }

    public required SampleOutcome Outcome { get; init; }

    public string? Member { get; init; }

    public string? Description { get; init; }

    public string? File { get; init; }

    public int Line { get; init; }

    public int FailingTests { get; init; }

    public int SelectedTests { get; init; }

    public int TotalTests { get; init; }

    public IReadOnlyList<string> Misses { get; init; } = [];

    public string? SkipReason { get; init; }
}

public sealed record MutationHarnessResult(IReadOnlyList<MutationSample> Samples)
{
    public int Usable => Samples.Count(s => s.Outcome != SampleOutcome.Skipped);

    public int Skipped => Samples.Count(s => s.Outcome == SampleOutcome.Skipped);

    public int Misses => Samples.Sum(s => s.Misses.Count);

    /// <summary>
    /// A run with no usable sample proves nothing, so it does not pass. Reporting "no misses"
    /// from zero observations is the failure mode that makes a correctness gate worthless.
    /// </summary>
    public bool Passed => Misses == 0 && Usable > 0;
}

/// <summary>
/// Mutation-based correctness checking.
/// </summary>
/// <remarks>
/// Real commits are almost all green, so replaying history yields very few failing tests to check
/// a selection against. Injected mutations produce unlimited ground truth instead: mutate a
/// method, select against that mutation as the diff, then run the whole suite. Any test that
/// fails but was not selected is a miss. The oracle is exact and the pass criterion is
/// unambiguous, which is what makes zero misses usable as a merge gate.
/// </remarks>
public sealed class MutationHarness(AnalysisOptions options, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task<MutationHarnessResult> RunAsync(int sampleCount, Random random, CancellationToken cancellationToken = default)
    {
        // The mutation lives in the working tree, so HEAD is the base by construction.
        var analysisOptions = options with { BaseRef = "HEAD", DefaultBranch = null, ForceFull = false };

        var survey = await new SolutionAnalyzer(analysisOptions with { ForceFull = true })
            .AnalyzeAsync(cancellationToken).ConfigureAwait(false);

        if (survey.Report.Projects.Count == 0)
        {
            throw new InvalidOperationException("no test projects were found in this solution");
        }

        var candidates = CollectCandidates(survey.Projects);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("no production source files were found to mutate");
        }

        _log($"{candidates.Count} candidate file(s), {survey.AllTests.Count} test(s), {sampleCount} sample(s)");

        var engine = new MutationEngine();
        var samples = new List<MutationSample>();

        for (var index = 1; index <= sampleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(await RunSampleAsync(index, candidates, engine, random, analysisOptions, survey.Report.Projects, cancellationToken)
                .ConfigureAwait(false));
        }

        return new MutationHarnessResult(samples);
    }

    private async Task<MutationSample> RunSampleAsync(
        int index,
        IReadOnlyList<string> candidates,
        MutationEngine engine,
        Random random,
        AnalysisOptions analysisOptions,
        IReadOnlyList<ProjectSelection> testProjects,
        CancellationToken cancellationToken)
    {
        var file = candidates[random.Next(candidates.Count)];
        var original = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        var mutation = engine.TryMutate(file, original, random);

        if (mutation is null)
        {
            return new MutationSample
            {
                Index = index,
                Outcome = SampleOutcome.Skipped,
                SkipReason = $"{Path.GetFileName(file)} offers no mutation site",
            };
        }

        var relative = Path.GetRelativePath(options.RepositoryRoot, file);

        try
        {
            await File.WriteAllTextAsync(file, mutation.MutatedText, cancellationToken).ConfigureAwait(false);
            _log($"[{index}] {mutation.Member} {mutation.Description}  ({relative}:{mutation.Line})");

            var outcome = await new SolutionAnalyzer(analysisOptions).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
            var report = outcome.Report;

            if (report.IsFullRun)
            {
                return new MutationSample
                {
                    Index = index,
                    Outcome = SampleOutcome.Skipped,
                    Member = mutation.Member,
                    Description = mutation.Description,
                    File = relative,
                    Line = mutation.Line,
                    SkipReason = "the analysis bailed out to a full run, so there is nothing to check",
                };
            }

            var selected = new HashSet<string>(report.Projects.SelectMany(p => p.Tests), StringComparer.Ordinal);
            var unfiltered = new HashSet<string>(
                report.Projects.Where(p => !p.Filtered).Select(p => p.Name), StringComparer.Ordinal);

            var suite = RunFullSuite(testProjects, GlobalJson.ReadTestMode(options.RepositoryRoot), cancellationToken);

            if (suite.Unobserved.Count > 0)
            {
                // Never report a clean sample from a run whose outcome could not be read: a
                // harness that says PASS when it saw nothing is worse than no harness.
                return new MutationSample
                {
                    Index = index,
                    Outcome = SampleOutcome.Skipped,
                    Member = mutation.Member,
                    Description = mutation.Description,
                    File = relative,
                    Line = mutation.Line,
                    SkipReason = $"no TRX results were produced for {string.Join(", ", suite.Unobserved)} - " +
                                 "the harness needs Microsoft.NET.Test.Sdk (VSTest) or " +
                                 "Microsoft.Testing.Extensions.TrxReport (Microsoft.Testing.Platform) to read outcomes",
                };
            }

            var misses = new List<string>();

            foreach (var (project, test) in suite.Failures)
            {
                // A project running unfiltered runs everything in it, so nothing there can be missed.
                if (unfiltered.Contains(project))
                {
                    continue;
                }

                var normalized = TrxParser.NormalizeTestName(test);
                if (!selected.Any(s => normalized.EndsWith(s, StringComparison.Ordinal) || s.EndsWith(normalized, StringComparison.Ordinal)))
                {
                    misses.Add(test);
                }
            }

            return new MutationSample
            {
                Index = index,
                Outcome = misses.Count == 0 ? SampleOutcome.Clean : SampleOutcome.Miss,
                Member = mutation.Member,
                Description = mutation.Description,
                File = relative,
                Line = mutation.Line,
                FailingTests = suite.Failures.Count,
                SelectedTests = report.SelectedTests,
                TotalTests = report.TotalTests,
                Misses = misses,
            };
        }
        finally
        {
            await File.WriteAllTextAsync(file, original, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private SuiteRun RunFullSuite(IReadOnlyList<ProjectSelection> projects, DotnetTestMode mode, CancellationToken cancellationToken)
    {
        var failures = new List<(string, string)>();
        var unobserved = new List<string>();

        foreach (var project in projects)
        {
            var resultsDirectory = Path.Combine(Path.GetTempPath(), "tia-verify-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(resultsDirectory);

            try
            {
                var arguments = new List<string> { "test" };

                if (mode == DotnetTestMode.MicrosoftTestingPlatform)
                {
                    arguments.Add("--project");
                }

                arguments.Add(project.ProjectPath);

                // TRX is the one result format both runners emit, which is what makes this work
                // across all four frameworks.
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

                ProcessRunner.Run("dotnet", arguments, options.RepositoryRoot, cancellationToken);

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

    /// <summary>
    /// One full-suite run. <paramref name="Unobserved"/> names the projects whose outcome could
    /// not be read, which has to be distinguished from "nothing failed".
    /// </summary>
    private sealed record SuiteRun(IReadOnlyList<(string Project, string Test)> Failures, IReadOnlyList<string> Unobserved);

    private static List<string> CollectCandidates(IReadOnlyList<ProjectDescriptor> projects)
    {
        var files = new List<string>();

        foreach (var project in projects.Where(p => !p.IsTestProject))
        {
            if (!Directory.Exists(project.Directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(project.Directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add(file);
            }
        }

        return files;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp directory is not worth failing a harness run over.
        }
    }
}
