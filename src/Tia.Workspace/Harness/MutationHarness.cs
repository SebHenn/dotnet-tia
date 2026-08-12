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

/// <param name="ProjectGranularity">
/// True when outcomes could only be read per project, not per test. Such a run can find a miss but
/// can never establish that there is none, so it must never be reported as a pass - see
/// <see cref="Passed"/>.
/// </param>
public sealed record MutationHarnessResult(IReadOnlyList<MutationSample> Samples, bool ProjectGranularity = false)
{
    public int Usable => Samples.Count(s => s.Outcome != SampleOutcome.Skipped);

    public int Skipped => Samples.Count(s => s.Outcome == SampleOutcome.Skipped);

    public int Misses => Samples.Sum(s => s.Misses.Count);

    /// <summary>
    /// A run with no usable sample proves nothing, so it does not pass. Reporting "no misses"
    /// from zero observations is the failure mode that makes a correctness gate worthless.
    /// </summary>
    /// <remarks>
    /// A project-granularity run is never a pass either, for the same reason in weaker form: it
    /// saw that a project failed, not which of its tests did, so "no miss found" and "no miss
    /// exists" are not the same sentence. It is still a useful gate - it can prove a miss - which
    /// is why it does not simply fail; the caller distinguishes the two.
    /// </remarks>
    public bool Passed => Misses == 0 && Usable > 0 && !ProjectGranularity;

    /// <summary>Nothing was found wrong, by whatever means this run had available.</summary>
    public bool FoundNoMiss => Misses == 0 && Usable > 0;
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

    /// <summary>
    /// Refuses to run against a working tree that already has modifications.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every sample diffs the working tree against <c>HEAD</c>, so a tracked file that was already
    /// modified is in *every* sample's diff alongside the injected mutation. The selection grows to
    /// cover it, and a gate whose selection is drifting toward everything cannot find a miss - it
    /// reports PASS either way. That is exactly how the byte-order-mark defect stayed invisible,
    /// and an uncommitted edit reproduces it deliberately.
    /// </para>
    /// <para>
    /// The second reason is the working tree itself. The harness restores what it mutated, but a
    /// run killed between the write and the restore leaves a mutated file behind - and against a
    /// dirty tree there is no way for anyone, including the harness, to tell that leftover from
    /// work in progress. The replay harness has refused to start on a dirty tree since it was
    /// written; the asymmetry was never intentional.
    /// </para>
    /// <para>
    /// Untracked files count here, which is where this differs from the replay harness. Replay
    /// excludes them because a checkout leaves them alone; the mutation harness has no checkout,
    /// and <see cref="Tia.Core.Diff.DiffResolver"/> deliberately adds untracked files to the diff
    /// so that a newly written test is not invisible. So an untracked file inflates a sample's
    /// selection exactly as a modified one does. Ignored paths - build output, the <c>.tia</c>
    /// cache this very run writes - are not reported by <c>git status</c> and do not count.
    /// </para>
    /// </remarks>
    internal static void RequireCleanWorkingTree(string repositoryRoot)
    {
        var status = ProcessRunner.Run("git", ["status", "--porcelain"], repositoryRoot);

        if (!status.Succeeded || status.StandardOutput.Trim().Length == 0)
        {
            return;
        }

        var modified = status.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Take(5)
            .ToList();

        throw new InvalidOperationException(
            "the working tree is not clean, and the harness mutates it in place. Every sample " +
            "diffs the working tree against HEAD, so these changes would be analysed alongside " +
            "each injected mutation: the selection would grow to cover them and a miss could no " +
            "longer be detected. Commit or stash them first. Changed: " +
            string.Join(", ", modified) + (modified.Count == 5 ? ", ..." : string.Empty));
    }

    /// <param name="allowProjectGranularity">
    /// Opt in to gating a repository whose test projects cannot write TRX. The harness then reads
    /// each project's exit code instead of its individual outcomes, which can prove a miss and
    /// never a pass. Off by default: a weaker gate that looked like the real one would put a clean
    /// verdict on a run that could not see what it claimed to check.
    /// </param>
    public async Task<MutationHarnessResult> RunAsync(
        int sampleCount,
        Random random,
        bool allowProjectGranularity = false,
        CancellationToken cancellationToken = default)
    {
        RequireCleanWorkingTree(options.RepositoryRoot);

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

        // One baseline run, before any mutation, to find out whether outcomes can be read at all.
        // Without it that answer arrives once per sample as "inconclusive", so a 60-sample run
        // spends an hour discovering something a single run could have said up front - and ends
        // with a verdict that proves nothing, which is easy to mistake for a verdict that passed.
        var preflight = RunFullSuite(survey.Report.Projects, GlobalJson.ReadTestMode(options.RepositoryRoot), cancellationToken);
        var projectGranularity = false;

        if (preflight.Unobserved.Count > 0)
        {
            if (!allowProjectGranularity)
            {
                throw new InvalidOperationException(UnobservableMessage(preflight.Unobserved, survey.Report.Projects));
            }

            projectGranularity = true;
            _log($"outcomes are unreadable for {string.Join(", ", preflight.Unobserved)}; " +
                 "falling back to each project's exit code, which can find a miss but never rule one out");
        }

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
            samples.Add(await RunSampleAsync(index, candidates, engine, random, analysisOptions, survey.Report.Projects, projectGranularity, cancellationToken)
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

        return new MutationHarnessResult(samples, projectGranularity);
    }

    /// <summary>
    /// A sample judged on exit codes alone, for a repository whose projects cannot write TRX.
    /// </summary>
    /// <remarks>
    /// The one sound inference available: a project that failed, none of whose tests the selection
    /// chose, contains a failing test that would not have run. That is a definite miss. The
    /// converse does not follow - a failed project with *some* tests selected may well have failed
    /// on a different test than the one selected - so this can never return evidence of a pass,
    /// only the absence of evidence against. <see cref="MutationHarnessResult.Passed"/> is what
    /// keeps that distinction from being quietly dropped at the end of the run.
    /// </remarks>
    private static MutationSample ProjectGranularitySample(
        int index,
        Mutation mutation,
        string relative,
        AnalysisReport report,
        SuiteRun suite,
        HashSet<string> unfiltered)
    {
        var selectedCounts = report.Projects.ToDictionary(p => p.Name, p => p.Tests.Count, StringComparer.Ordinal);

        var misses = suite.FailedProjects
            .Where(p => !unfiltered.Contains(p) && selectedCounts.GetValueOrDefault(p) == 0)
            .Select(p => $"{p} (whole project: individual outcomes were not readable)")
            .ToList();

        return new MutationSample
        {
            Index = index,
            Outcome = misses.Count == 0 ? SampleOutcome.Clean : SampleOutcome.Miss,
            Member = mutation.Member,
            Description = mutation.Description,
            File = relative,
            Line = mutation.Line,
            FailingTests = suite.FailedProjects.Count,
            SelectedTests = report.SelectedTests,
            TotalTests = report.TotalTests,
            Misses = misses,
        };
    }

    /// <summary>
    /// Names the package each unobservable project is missing, rather than the two candidates and
    /// a guess. TRX is the one format both runners emit, and which package writes it depends on the
    /// runner that project uses - so the answer is per project, and the harness already knows it.
    /// </summary>
    internal static string UnobservableMessage(
        IReadOnlyList<string> unobserved,
        IReadOnlyList<ProjectSelection> projects)
    {
        var lines = unobserved.Select(name =>
        {
            var runner = projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))?.Runner;
            var package = string.Equals(runner, nameof(TestRunner.MicrosoftTestingPlatform), StringComparison.Ordinal)
                ? "Microsoft.Testing.Extensions.TrxReport"
                : "Microsoft.NET.Test.Sdk";

            return $"    {name}: add {package}";
        });

        return "The harness cannot read the outcome of every test project, so no sample could prove " +
               "anything and the run is refused rather than reported as inconclusive one sample at a " +
               "time. It reads results from TRX, which needs a reporter referenced by each project:" +
               Environment.NewLine + string.Join(Environment.NewLine, lines);
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
        bool projectGranularity,
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

            if (suite.Unobserved.Count > 0 && !projectGranularity)
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

            if (projectGranularity)
            {
                return ProjectGranularitySample(index, mutation, relative, report, suite, unfiltered);
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

    private SuiteRun RunFullSuite(IReadOnlyList<ProjectSelection> projects, DotnetTestMode mode, CancellationToken cancellationToken) =>
        new SuiteRunner(options.RepositoryRoot, _log).RunAll(projects, mode, cancellationToken);

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

}
