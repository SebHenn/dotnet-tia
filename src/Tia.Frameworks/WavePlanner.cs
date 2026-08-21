using Tia.Core.Model;
using Tia.Frameworks.Dialects;

namespace Tia.Frameworks;

/// <summary>How a project's selection is divided between two invocations, or why it is not.</summary>
public sealed record WavePlan
{
    public static WavePlan Whole(string reason) => new() { Split = false, Reason = reason };

    public required bool Split { get; init; }

    /// <summary>Why the selection runs in one invocation. Null when it does not.</summary>
    public string? Reason { get; init; }

    public int FirstWaveTests { get; init; }

    public IReadOnlyList<string> FirstWaveArguments { get; init; } = [];

    public IReadOnlyList<string> RemainderArguments { get; init; } = [];
}

/// <summary>
/// Divides a ranked selection into a nearest slice and the rest, so a likely failure surfaces
/// before the whole selection has run.
/// </summary>
/// <remarks>
/// <para>
/// Ranking without this changes nothing: <c>run</c> invokes <c>dotnet test</c> once per project and
/// the runner picks the order inside it. The rank computed in
/// <see cref="Tia.Core.Analysis.TestSelection"/> only becomes visible once the nearest tests are
/// handed over as an invocation of their own.
/// </para>
/// <para>
/// Two hazards, and both are checked rather than argued. A filter built for a subset can match
/// tests in the other subset, which would run them twice - the exact opposite of the point. And a
/// pair of filters could between them match something the single filter would not have. Neither is
/// a missed test, so neither shows up in the mutation gate, which is why they are refused here
/// instead: <see cref="IFilterDialect.ExtraMatches"/> is each dialect's own account of what its
/// filter matches, and both properties are decided against it before a split is offered.
/// </para>
/// <para>
/// Waves are taken at class boundaries. A class split across both waves is what makes overlap
/// likely in the first place - a contains-match on <c>Ns.Cls.Add</c> also matches
/// <c>Ns.Cls.AddRange</c>, and a collapsed class filter matches every method in it - so keeping a
/// class whole is what lets the checks above usually pass rather than usually refuse.
/// </para>
/// </remarks>
public static class WavePlanner
{
    /// <summary>
    /// How much of the selection the first wave aims at.
    /// </summary>
    /// <remarks>
    /// From the only evidence there is: across the cartographer mutation samples that broke a
    /// selected test, the first failure sat 24 % of the way into the ranked order. Seven
    /// observations, so this is a starting point to be re-measured, not a tuned constant.
    /// </remarks>
    public const double DefaultWaveFraction = 0.25;

    /// <summary>
    /// Below this, a second invocation cannot pay. A handful of tests finish inside the process
    /// start they would have to be split across.
    /// </summary>
    public const int MinimumSelection = 8;

    public static WavePlan Plan(
        IFilterDialect dialect,
        IReadOnlyList<TestMethod> ranked,
        IReadOnlyList<TestMethod> allInProject,
        int? maxFilterLength = null,
        double waveFraction = DefaultWaveFraction)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(allInProject);

        var lengthLimit = maxFilterLength ?? FilterPlanner.DefaultMaxFilterLength;

        if (ranked.Count < MinimumSelection)
        {
            return WavePlan.Whole(
                $"only {ranked.Count} test(s) selected, below the {MinimumSelection} it takes for a second invocation to pay");
        }

        var (wave, remainder) = Divide(ranked, waveFraction);

        if (remainder.Count == 0)
        {
            return WavePlan.Whole("every selected test is in one class, so there is no nearer slice to run first");
        }

        // The wave is meant to be a probe, not the run. Class boundaries can push it past half when
        // the nearest class is large, and at that point the second invocation buys almost nothing.
        if (wave.Count * 2 > ranked.Count)
        {
            return WavePlan.Whole(
                $"the nearest class alone is {wave.Count} of {ranked.Count} selected tests, so the first wave would be most of the run");
        }

        var waveArguments = dialect.BuildArguments(wave, allInProject);
        var remainderArguments = dialect.BuildArguments(remainder, allInProject);

        if (waveArguments.Count == 0 || remainderArguments.Count == 0)
        {
            return WavePlan.Whole($"the {dialect.Name} dialect cannot express one of the two waves as a filter");
        }

        var longest = Math.Max(Length(waveArguments), Length(remainderArguments));
        if (longest > lengthLimit)
        {
            return WavePlan.Whole(
                $"a wave's filter would be {longest} characters, above the {lengthLimit} safe command-line limit for this platform");
        }

        if (WhyUnsafe(dialect, wave, remainder, ranked, allInProject) is { } unsafeReason)
        {
            return WavePlan.Whole(unsafeReason);
        }

        return new WavePlan
        {
            Split = true,
            FirstWaveTests = wave.Count,
            FirstWaveArguments = waveArguments,
            RemainderArguments = remainderArguments,
        };
    }

    /// <summary>
    /// Takes whole classes off the front of the ranked order until the wave reaches its target
    /// size. Both halves keep the ranked order, because the rank is still what decides what runs
    /// first inside each invocation for any runner that honours the order it is given.
    /// </summary>
    private static (List<TestMethod> Wave, List<TestMethod> Remainder) Divide(
        IReadOnlyList<TestMethod> ranked,
        double waveFraction)
    {
        var target = Math.Max(1, (int)Math.Ceiling(ranked.Count * waveFraction));

        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var test in ranked)
        {
            var key = ClassCollapser.FullClassName(test);
            sizes[key] = sizes.GetValueOrDefault(key) + 1;
        }

        var waveClasses = new HashSet<string>(StringComparer.Ordinal);
        var taken = 0;

        foreach (var test in ranked)
        {
            if (taken >= target)
            {
                break;
            }

            var key = ClassCollapser.FullClassName(test);
            if (waveClasses.Add(key))
            {
                taken += sizes[key];
            }
        }

        var wave = new List<TestMethod>(taken);
        var remainder = new List<TestMethod>(ranked.Count - taken);

        foreach (var test in ranked)
        {
            (waveClasses.Contains(ClassCollapser.FullClassName(test)) ? wave : remainder).Add(test);
        }

        return (wave, remainder);
    }

    /// <summary>
    /// Why these two filters must not be used in place of one, or null when they may be.
    /// </summary>
    /// <remarks>
    /// Both questions are asked of the dialect rather than reasoned about here. A second model of
    /// what a filter matches, kept beside the one the dialects already maintain, is a model that
    /// will eventually disagree with them - and it would disagree silently, because running a test
    /// twice and running one extra test are both green.
    /// </remarks>
    private static string? WhyUnsafe(
        IFilterDialect dialect,
        IReadOnlyList<TestMethod> wave,
        IReadOnlyList<TestMethod> remainder,
        IReadOnlyList<TestMethod> ranked,
        IReadOnlyList<TestMethod> allInProject)
    {
        var waveNames = Names(wave);
        var remainderNames = Names(remainder);

        var waveExtra = dialect.ExtraMatches(wave, allInProject);
        var remainderExtra = dialect.ExtraMatches(remainder, allInProject);

        var overlap = waveExtra.Count(t => remainderNames.Contains(t.FullyQualifiedName)) +
                      remainderExtra.Count(t => waveNames.Contains(t.FullyQualifiedName));

        if (overlap > 0)
        {
            return $"the two waves' filters overlap on {overlap} test(s), which would run them twice";
        }

        // Splitting may only ever run a subset of what one filter would have run. Establishing that
        // by argument is not enough: the argument depends on how each dialect collapses classes,
        // which is a detail the dialects are free to change.
        var single = Names(ranked);
        single.UnionWith(dialect.ExtraMatches(ranked, allInProject).Select(t => t.FullyQualifiedName));

        var beyond = waveExtra.Concat(remainderExtra)
            .Select(t => t.FullyQualifiedName)
            .Where(name => !single.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Count();

        return beyond > 0
            ? $"the two waves' filters would together match {beyond} test(s) that one filter would not have run"
            : null;
    }

    private static HashSet<string> Names(IEnumerable<TestMethod> tests) =>
        new(tests.Select(t => t.FullyQualifiedName), StringComparer.Ordinal);

    /// <summary>Counted the same way <see cref="FilterPlanner"/> counts it, quoting included.</summary>
    private static int Length(IReadOnlyList<string> arguments) => arguments.Sum(a => a.Length + 3);
}
