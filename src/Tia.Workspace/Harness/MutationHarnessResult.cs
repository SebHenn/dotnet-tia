
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

/// <summary>One mutation the harness can make: a file, and a site numbered as <see cref="MutationEngine"/> numbers them.</summary>
internal sealed record MutationSite(string File, int Site);

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
