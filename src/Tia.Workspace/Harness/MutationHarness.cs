using System.Security.Cryptography;
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

    /// <summary>
    /// Whether a test that failed under a mutation was in the selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This single comparison is what "zero misses" means, so it is worth being exact about. It
    /// used to be a *symmetric suffix* match - <c>failing.EndsWith(selected) ||
    /// selected.EndsWith(failing)</c> - which was a workaround for TRX reporting bare method names
    /// for NUnit. That is fixed at the source now: <see cref="TrxParser"/> qualifies every result
    /// from its test definition.
    /// </para>
    /// <para>
    /// The workaround outlived its cause and was dangerous on its own terms: a failing
    /// <c>Foo.CounterTests.Increments</c> matched a selected <c>Bar.OtherTests.Increments</c> and
    /// the sample was recorded Clean. A gate whose comparison is loose reports PASS for misses,
    /// which is the one failure mode that makes every number it produces worthless.
    /// </para>
    /// <para>
    /// Both sides are normalised: a parameterised test is selected whole, so the selection holds
    /// <c>Ns.Cls.Method</c> while the runner reports <c>Ns.Cls.Method(1, "a")</c>.
    /// </para>
    /// </remarks>
    public static bool IsSelected(string failingTest, IReadOnlySet<string> selected) =>
        selected.Contains(TrxParser.NormalizeTestName(failingTest)) ||
        selected.Contains(failingTest);

    public async Task<MutationHarnessResult> RunAsync(int sampleCount, Random random, CancellationToken cancellationToken = default)
    {
        // The mutation lives in the working tree, so HEAD is the base by construction.
        var analysisOptions = options with { BaseRef = "HEAD", DefaultBranch = null, ForceFull = false };

        var survey = await new SolutionAnalyzer(analysisOptions with { ForceFull = true })
            .AnalyzeAsync(cancellationToken).ConfigureAwait(false);

        if (survey.Report.Projects.Count == 0)
        {
            // Analysis falls back to a full run rather than throwing, so a solution that could not
            // be opened at all arrives here looking exactly like one with no tests in it. Carrying
            // the reason through turns "no test projects were found" - which sent one debugging
            // session after the wrong problem - back into "solution file not found".
            var reasons = survey.Report.FullRunReasons.Count > 0
                ? " (" + string.Join("; ", survey.Report.FullRunReasons) + ")"
                : string.Empty;

            throw new InvalidOperationException($"no test projects were found in this solution{reasons}");
        }

        var candidates = CollectCandidates(survey.Projects);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("no production source files were found to mutate");
        }

        _log($"{candidates.Count} candidate file(s), {survey.AllTests.Count} test(s), {sampleCount} sample(s)");

        var engine = new MutationEngine();
        var samples = new List<MutationSample>();

        // Every sample assumes the working tree it starts from is the one the last sample handed
        // back. When that stopped being true - a stripped byte-order mark that never went back on
        // - the diff grew with every sample and so did the selection, and a gate that selects more
        // and more cannot find a miss. Nothing in the output said so, so the harness now checks.
        var before = Fingerprint(candidates);

        for (var index = 1; index <= sampleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(await RunSampleAsync(index, candidates, engine, random, analysisOptions, survey.Report.Projects, cancellationToken)
                .ConfigureAwait(false));

            var drifted = Drift(candidates, before);
            if (drifted is not null)
            {
                throw new InvalidOperationException(
                    $"sample {index} did not restore {Path.GetRelativePath(options.RepositoryRoot, drifted)}. " +
                    "Every later sample would have analysed that leftover change as well, so the run is abandoned " +
                    "rather than reported.");
            }
        }

        return new MutationHarnessResult(samples);
    }

    /// <summary>
    /// A content hash of every file a sample is allowed to touch. Not length, and not the
    /// timestamp: a restore rewrites the file so the timestamp always moves, and the commonest
    /// mutation of all - swapping <c>+</c> for <c>-</c> - does not change the length.
    /// </summary>
    internal static Dictionary<string, string> Fingerprint(IReadOnlyList<string> candidates)
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in candidates)
        {
            state[file] = File.Exists(file)
                ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))
                : "absent";
        }

        return state;
    }

    /// <summary>The first file that is not as it was, or null when the tree is intact.</summary>
    internal static string? Drift(IReadOnlyList<string> candidates, Dictionary<string, string> before)
    {
        foreach (var (file, hash) in Fingerprint(candidates))
        {
            if (!before.TryGetValue(file, out var expected) || !string.Equals(expected, hash, StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
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

        // Bytes, not text: the file has to go back exactly as it was. See SourceFile.
        var source = SourceFile.FromBytes(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
        var mutation = engine.TryMutate(file, source.Text, random);

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
            await File.WriteAllBytesAsync(file, source.Rewrite(mutation.MutatedText), cancellationToken).ConfigureAwait(false);
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

                if (!IsSelected(test, selected))
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
            // Byte-for-byte what was there, so the working tree is exactly as the sample found it.
            await File.WriteAllBytesAsync(file, source.Bytes, CancellationToken.None).ConfigureAwait(false);
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

                ProcessRunner.Run("dotnet", arguments, options.RepositoryRoot, cancellationToken: cancellationToken);

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
