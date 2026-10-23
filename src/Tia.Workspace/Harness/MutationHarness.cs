using System.Diagnostics;
using System.Security.Cryptography;
using Tia.Core.Caching;
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

    /// <summary>
    /// The suite was killed for running past its budget, so nothing can be concluded about the
    /// selection. Almost always a mutation that stopped a loop terminating.
    /// </summary>
    /// <remarks>
    /// Its own outcome rather than a <see cref="Skipped"/> with a different sentence, because the
    /// two mean opposite things about the sample. Skipped is "there was nothing here to check";
    /// this is "there was something here and the harness could not finish checking it", which is
    /// the shape that should make a reader look rather than move on.
    /// </remarks>
    TimedOut,

    /// <summary>
    /// A project of the suite reached the heap limit, so its outcomes were not read. Almost always
    /// a mutation that made a loop allocate without end - the memory twin of <see cref="TimedOut"/>.
    /// </summary>
    /// <remarks>
    /// Not usable and never a miss. Under the limit an <c>OutOfMemoryException</c> lands on
    /// whichever allocation reaches it first, which can be any test running alongside the runaway
    /// one, so the failures such a run reports say nothing about what the mutation broke. Before the
    /// limit existed this sample did not end at all: on NodaTime a test host grew to 15 GB in three
    /// minutes and took the machine running the harness down with it.
    /// </remarks>
    OutOfMemory,
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

    /// <summary>
    /// Where the earliest failing test sits in the order the selection would run in, one-based, or
    /// zero when nothing failed.
    /// </summary>
    /// <remarks>
    /// The measurement that says whether ranking tests by distance from the change is worth
    /// anything, and it costs nothing to take: the harness has already injected a fault and
    /// already knows which tests it broke, so the position of the first of them in the ordered
    /// selection is arithmetic on data in hand rather than a second run.
    /// </remarks>
    public int FirstFailurePosition { get; init; }

    /// <summary>How many tests the ordered run would have had to get through in total.</summary>
    public int OrderedTests { get; init; }

    public string? SkipReason { get; init; }
}

/// <param name="ProjectGranularity">
/// True when outcomes could only be read per project, not per test. Such a run can find a miss but
/// can never establish that there is none, so it must never be reported as a pass - see
/// <see cref="Passed"/>.
/// </param>
public sealed record MutationHarnessResult(IReadOnlyList<MutationSample> Samples, bool ProjectGranularity = false)
{
    /// <summary>
    /// Samples that could say something. A timed-out sample cannot: the suite was killed before it
    /// reported, so counting it would be counting an observation nobody made. Nor can one that ran
    /// out of memory, whose outcomes exist and are not to be believed.
    /// </summary>
    public int Usable => Samples.Count(s => s.Outcome is not (SampleOutcome.Skipped or SampleOutcome.TimedOut or SampleOutcome.OutOfMemory));

    public int Skipped => Samples.Count(s => s.Outcome == SampleOutcome.Skipped);

    public int TimedOut => Samples.Count(s => s.Outcome == SampleOutcome.TimedOut);

    public int OutOfMemory => Samples.Count(s => s.Outcome == SampleOutcome.OutOfMemory);

    public int Misses => Samples.Sum(s => s.Misses.Count);

    /// <summary>
    /// Usable samples whose mutation broke no test at all.
    /// </summary>
    /// <remarks>
    /// Not part of the gate, and not a statement about selection: a selection cannot miss a
    /// failure that does not exist, so these samples are clean and correctly so. They are a
    /// statement about the suite instead, and this harness is the only thing that can make it,
    /// because it is the only thing that runs the whole suite against a build known to be broken.
    /// Left implicit, it had to be counted by hand from the per-sample lines - and one of the six
    /// counted that way in issue #21 was a live defect that every assertion passed over.
    /// </remarks>
    public IReadOnlyList<MutationSample> Unobserved =>
        [.. Samples.Where(s => s.Outcome == SampleOutcome.Clean && s.FailingTests == 0)];

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

    /// <summary>Samples where a failing test actually appears in the ordered selection.</summary>
    public IReadOnlyList<MutationSample> Ordered =>
        [.. Samples.Where(s => s.FirstFailurePosition > 0 && s.OrderedTests > 0)];

    /// <summary>
    /// How far into an ordered run the first failure appeared, as a fraction of the run, against
    /// the same figure for an unordered one.
    /// </summary>
    /// <remarks>
    /// The unordered comparison is the expectation under a uniformly random order, which for a
    /// single failing test among <c>n</c> is <c>(n + 1) / 2n</c> - just over a half. It is an
    /// expectation rather than a measurement because no runner promises an order, so there is no
    /// second arm to run: whatever order a runner happens to use is not a baseline anybody could
    /// reproduce. Reported as such, and never as a measured before-and-after.
    /// </remarks>
    public (double Ordered, double Unordered, int Samples)? TimeToFirstFailure
    {
        get
        {
            var usable = Ordered;
            return usable.Count == 0
                ? null
                : (usable.Average(s => (double)s.FirstFailurePosition / s.OrderedTests),
                   usable.Average(s => (s.OrderedTests + 1) / (2.0 * s.OrderedTests)),
                   usable.Count);
        }
    }
}

/// <summary>
/// A run the harness stopped part-way, carrying the samples it had already checked.
/// </summary>
/// <remarks>
/// Every other refusal happens before the first mutation, so it has nothing to report and
/// <c>verify</c> calls it inconclusive. This one happens after a sample, and the samples before it
/// - and that sample itself - ran against the tree they were meant to. A miss among them is a real
/// miss, and dropping it with the rest of the run would report a found defect as "could not tell"
/// (issue #36). An <see cref="InvalidOperationException"/> still, so a caller that only knows
/// refusals keeps working.
/// </remarks>
public sealed class MutationRunAbandonedException(string message, MutationHarnessResult partial)
    : InvalidOperationException(message)
{
    /// <summary>The samples checked before the run was abandoned.</summary>
    public MutationHarnessResult Partial { get; } = partial;
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
/// <param name="notice">
/// Where to say that the harness changed the working tree on its own account - putting back a
/// mutation a killed run left behind. Separate from <paramref name="log"/>, which <c>--json</c>
/// silences, because a file rewritten without a word is not something to keep quiet about.
/// Defaults to the log.
/// </param>
public sealed class MutationHarness(AnalysisOptions options, Action<string>? log = null, Action<string>? notice = null)
{
    private readonly Action<string> _log = log ?? (_ => { });
    private readonly Action<string> _notice = notice ?? log ?? (_ => { });

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
    /// The one-based position of the earliest failing test in the ordered selection, or zero when
    /// no failing test appears in it.
    /// </summary>
    /// <remarks>
    /// Zero for "no failing test is in the order" rather than for "nothing failed", and the two
    /// are not the same thing: a sample whose failures were all missed has failures and no
    /// position. Both are excluded from the average, because neither says anything about how well
    /// the order works - the first had nothing to find and the second is a miss, which is a
    /// different and worse problem already counted elsewhere.
    /// </remarks>
    public static int FirstFailureAt(IReadOnlyList<string> ordered, IReadOnlyList<(string Project, string Test)> failures)
    {
        var failing = new HashSet<string>(failures.Select(f => TrxParser.NormalizeTestName(f.Test)), StringComparer.Ordinal);
        if (failing.Count == 0)
        {
            return 0;
        }

        for (var position = 0; position < ordered.Count; position++)
        {
            if (failing.Contains(ordered[position]) || failing.Contains(TrxParser.NormalizeTestName(ordered[position])))
            {
                return position + 1;
            }
        }

        return 0;
    }

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
    /// <para>
    /// A <c>git status</c> that fails is a refusal too (#38). It used to return early, so a check
    /// whose whole purpose is to stop the harness mutating a tree it cannot account for let it do
    /// exactly that whenever git could not say what the tree held - a corrupt index, a lock held
    /// by another process, a directory git does not recognise.
    /// </para>
    /// </remarks>
    /// <param name="cacheDirectory">
    /// Left out like <c>.tia/</c>, which git ignores: a custom <c>--cache-dir</c> that it does not
    /// ignore would otherwise make every tree this harness has itself run in look dirty (#44).
    /// </param>
    internal static void RequireCleanWorkingTree(string repositoryRoot, string? cacheDirectory = null)
    {
        var excluded = CacheDirectory.RelativeTo(repositoryRoot, repositoryRoot, cacheDirectory ?? ".tia");
        var status = ProcessRunner.Run(
            "git",
            excluded is null ? ["status", "--porcelain"] : ["status", "--porcelain", "--", ":/", $":(exclude,literal){excluded}"],
            repositoryRoot);

        if (!status.Succeeded)
        {
            throw new UnverifiableWorkingTreeException(
                $"`git status` failed in {repositoryRoot} (exit {status.ExitCode}), so the harness cannot tell " +
                "whether the working tree is clean, and it mutates that tree in place: " +
                status.StandardError.Trim());
        }

        if (status.StandardOutput.Trim().Length == 0)
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
        // First, so that a mutation a killed run left behind is put back rather than reported as a
        // dirty tree - whose advice is to commit it.
        if (MutationSentinel.Recover(options.RepositoryRoot, options.CacheDirectory) is { } recovered)
        {
            _notice(recovered);
        }

        RequireCleanWorkingTree(options.RepositoryRoot, options.CacheDirectory);

        // The mutation lives in the working tree, so HEAD is the base by construction.
        var analysisOptions = options with { BaseRef = "HEAD", DefaultBranch = null, ForceFull = false };

        var survey = await new SolutionAnalyzer(analysisOptions with { ForceFull = true })
            .AnalyzeAsync(cancellationToken).ConfigureAwait(false);

        // Before the count below, which a failed load satisfies: the test projects are all there,
        // they simply hold nothing. Every sample would then mutate, run a suite of no tests, find
        // no failure and report a clean check - twenty-five confirmations that nothing was missed
        // out of a set with nothing in it.
        if (survey.Report.WorkspaceUnusable)
        {
            throw new InvalidOperationException(
                "the workspace did not resolve, so there is no suite to gate: " +
                string.Join("; ", survey.Report.LoadFailures));
        }

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
        var testMode = GlobalJson.ReadTestMode(options.RepositoryRoot);
        var baselineStarted = Stopwatch.GetTimestamp();
        var preflight = RunFullSuite(survey.Report.Projects, testMode, timeout: null, heapLimit: null, cancellationToken);
        var budget = Budget(Stopwatch.GetElapsedTime(baselineStarted));
        var heapLimit = HeapLimit(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        _log($"baseline suite took {Stopwatch.GetElapsedTime(baselineStarted).TotalSeconds:0}s; " +
             $"each mutated run is capped at {budget.TotalSeconds:0}s per project and " +
             (Environment.GetEnvironmentVariable(SuiteRunner.HeapLimitVariable) is { Length: > 0 } inherited
                 ? $"{SuiteRunner.HeapLimitVariable}={inherited} per process, as set"
                 : $"{SuiteRunner.FormatBytes(heapLimit)} of heap per process"));

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
            samples.Add(await RunSampleAsync(index, candidates, engine, random, analysisOptions, survey.Report.Projects, projectGranularity, budget, heapLimit, cancellationToken)
                .ConfigureAwait(false));

            var drifted = Drift(candidates, before);
            if (drifted is not null)
            {
                throw new MutationRunAbandonedException(
                    $"sample {index} did not restore {Path.GetRelativePath(options.RepositoryRoot, drifted)}. " +
                    "Every later sample would have analysed that leftover change as well, so the run is abandoned " +
                    "rather than reported.",
                    new MutationHarnessResult(samples, projectGranularity));
            }
        }

        return new MutationHarnessResult(samples, projectGranularity);
    }

    /// <summary>
    /// How long one project may run under a mutation, from how long the whole baseline took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived rather than configured, because the only honest constant here is a multiple. A
    /// mutation changes one expression; it does not make a terminating suite four times slower, so
    /// four times the baseline separates "unlucky and slow" from "will never finish" on a fast
    /// repository and a slow one alike.
    /// </para>
    /// <para>
    /// The floor exists because the baseline is the whole suite and the budget is per project: on a
    /// repository whose suite takes two seconds, four times it would kill a project that merely had
    /// to restore something. The ceiling exists because the point is to bound a hang, and a
    /// three-hour wait is a hang whatever the multiple says.
    /// </para>
    /// <para>
    /// Found by hanging. Gating tia itself, a sample dropped the statement that stores each learned
    /// set in the type-flow fixpoint, so the fixpoint re-derived the same delta for ever. The run
    /// sat for three hours at 38 seconds of CPU and produced no verdict at all - not a miss, not a
    /// pass, and not a sample.
    /// </para>
    /// </remarks>
    internal static TimeSpan Budget(TimeSpan baseline)
    {
        var scaled = baseline * 4;

        if (scaled < TimeSpan.FromMinutes(2))
        {
            return TimeSpan.FromMinutes(2);
        }

        return scaled > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : scaled;
    }

    /// <summary>
    /// How much heap one process may hold under a mutation, from how much memory the machine has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A quarter, because the limit is per process and a run is several of them. A multi-targeted
    /// test project runs one host per framework in parallel, and the harness, MSBuild and the
    /// compiler server sit alongside - two runaway hosts at a quarter each still leave the machine
    /// standing. The floor is for small machines, where a quarter would starve a suite that is
    /// merely ordinary.
    /// </para>
    /// <para>
    /// The baseline run is not limited. There is no mutation to blame yet, and a suite that
    /// legitimately needs more should be able to say so by failing loudly there, not by turning
    /// every later sample into an out-of-memory one. A suite that needs more under a mutation than
    /// this allows is reported sample by sample, never as clean, and exporting
    /// <c>DOTNET_GCHeapHardLimit</c> before running replaces this figure.
    /// </para>
    /// </remarks>
    internal static long HeapLimit(long totalAvailableBytes) =>
        Math.Max(totalAvailableBytes / 4, 2L * 1024 * 1024 * 1024);

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

    /// <remarks>
    /// Internal so that the order around the mutation can be tested without a solution: the
    /// record before the write, the restore before the record goes (#38). A repository with no
    /// solution fails the analysis inside the sample, which is enough to watch both ends.
    /// </remarks>
    internal async Task<MutationSample> RunSampleAsync(
        int index,
        IReadOnlyList<string> candidates,
        MutationEngine engine,
        Random random,
        AnalysisOptions analysisOptions,
        IReadOnlyList<ProjectSelection> testProjects,
        bool projectGranularity,
        TimeSpan budget,
        long heapLimit,
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
        var mutated = source.Rewrite(mutation.MutatedText);
        var sentinel = MutationSentinel.Location(options.RepositoryRoot, options.CacheDirectory);

        // Before the write and outside the try: if this fails nothing has been mutated, and a
        // mutation without its record is the one thing the next run could not undo.
        MutationSentinel.Write(sentinel, relative, source.Bytes, mutated);

        try
        {
            await File.WriteAllBytesAsync(file, mutated, cancellationToken).ConfigureAwait(false);
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
                    // Carrying the reason, not just the fact. `analyze` has always printed why it
                    // widened - "Directory.Build.props changed - it applies to every project
                    // beneath it" - and dropping that here left the summary below guessing at a
                    // dirty working tree, which sent a reader to `git status` on a clean one.
                    SkipReason = report.FullRunReasons.Count > 0
                        ? "the analysis bailed out to a full run, so there is nothing to check: " +
                          string.Join("; ", report.FullRunReasons)
                        : "the analysis bailed out to a full run, so there is nothing to check",
                };
            }

            // Projects in report order, tests in rank order within each: exactly the order `run`
            // executes in, so a position in this list is a position in the run.
            var ordered = report.Projects.SelectMany(p => p.Tests).ToList();
            var selected = new HashSet<string>(ordered, StringComparer.Ordinal);
            var unfiltered = new HashSet<string>(
                report.Projects.Where(p => !p.Filtered).Select(p => p.Name), StringComparer.Ordinal);

            var suite = RunFullSuite(testProjects, GlobalJson.ReadTestMode(options.RepositoryRoot), budget, heapLimit, cancellationToken);

            // Checked before the unobserved branch below, which would otherwise absorb it and tell
            // the reader to install a TRX reporter they already have.
            if (suite.TimedOut.Count > 0)
            {
                return new MutationSample
                {
                    Index = index,
                    Outcome = SampleOutcome.TimedOut,
                    Member = mutation.Member,
                    Description = mutation.Description,
                    File = relative,
                    Line = mutation.Line,
                    SkipReason = $"{string.Join(", ", suite.TimedOut)} ran past {budget.TotalSeconds:0}s and was killed - " +
                                 "almost always a mutation that stopped a loop terminating, which says nothing about the selection",
                };
            }

            // Also before the unobserved branch, for the same reason, and before any failure is
            // read: these failures exist and are the limit's doing, not the mutation's.
            if (suite.OutOfMemory.Count > 0)
            {
                return new MutationSample
                {
                    Index = index,
                    Outcome = SampleOutcome.OutOfMemory,
                    Member = mutation.Member,
                    Description = mutation.Description,
                    File = relative,
                    Line = mutation.Line,
                    SkipReason = $"{string.Join(", ", suite.OutOfMemory)} reached the heap limit - almost always a mutation " +
                                 "that made a loop allocate without end, which says nothing about the selection",
                };
            }

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
                FirstFailurePosition = FirstFailureAt(ordered, suite.Failures),
                OrderedTests = ordered.Count,
            };
        }
        finally
        {
            // Byte-for-byte what was there, so the working tree is exactly as the sample found it.
            // This still does the work; the record only covers the kill that skips this block. It
            // goes once the restore has succeeded, and not before: a restore that threw leaves it
            // for the next run.
            await File.WriteAllBytesAsync(file, source.Bytes, CancellationToken.None).ConfigureAwait(false);
            MutationSentinel.Clear(sentinel);
        }
    }

    private SuiteRun RunFullSuite(
        IReadOnlyList<ProjectSelection> projects,
        DotnetTestMode mode,
        TimeSpan? timeout,
        long? heapLimit,
        CancellationToken cancellationToken) =>
        new SuiteRunner(options.RepositoryRoot, _log).RunAll(projects, mode, timeout, heapLimit, cancellationToken);

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
