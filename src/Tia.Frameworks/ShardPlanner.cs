using System.Globalization;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks.Dialects;

namespace Tia.Frameworks;

/// <summary>One shard of <paramref name="Count"/>, numbered from 1.</summary>
public sealed record ShardSpec(int Index, int Count)
{
    /// <summary>Reads <c>i/n</c>, or returns null with the reason in <paramref name="error"/>.</summary>
    public static ShardSpec? Parse(string text, out string? error)
    {
        var parts = text.Split('/');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) &&
            count is >= 1 and <= 256 && index >= 1 && index <= count)
        {
            error = null;
            return new ShardSpec(index, count);
        }

        error = $"'{text}' is not a shard. Expected i/n with 1 <= i <= n <= 256, such as 2/4.";
        return null;
    }

    public override string ToString() => $"{Index}/{Count}";
}

/// <summary>
/// Splits what a report would run across <c>n</c> CI jobs, and keeps the part one job runs (#73).
/// </summary>
/// <remarks>
/// <para>
/// No coordination between the jobs, so the split has to be a pure function of what every job
/// computes alike: the report and the tests discovered for the same commit. Every input is sorted
/// before it is used, and the filter-length limit is a fixed number rather than the platform's,
/// because a shard on Windows and a shard on Linux that disagreed about whether a filter fits would
/// disagree about who runs a project - and the project would run twice or not at all.
/// </para>
/// <para>
/// The unit is the test class. A class split across jobs would be set up in each, and a filter
/// naming whole classes collapses to a fraction of the length. Classes are dealt largest first to
/// whichever shard has the fewest tests so far, which is within a third of the best possible
/// balance and needs no search. A share whose filter is too long for the command line is passed in a
/// file, like any other (#90). A project whose share of some shard cannot be written as a filter
/// that fits or goes in a file, and a project with nothing to split, goes to one shard whole.
/// </para>
/// <para>
/// The invariant, and what the tests check: the shards are disjoint, and between them they run
/// exactly what the report would have run. A filter may match more than its share - the dialects
/// widen - so a test can run in two shards; never in none.
/// </para>
/// </remarks>
public static class ShardPlanner
{
    /// <summary>The longest filter a shard may emit unless the caller set one.</summary>
    /// <remarks>Windows' limit, chosen everywhere, so that every job reaches the same answer.</remarks>
    public const int DefaultMaxFilterLength = 24_000;

    private sealed record Unit(int Project, string Name, IReadOnlyList<TestMethod> Tests, double Weight);

    /// <param name="durations">
    /// A record to balance by (#91): a class weighs what its tests took, and a test the record has
    /// never seen weighs the record's typical test. Null balances by test count. Every shard must
    /// be given the same record, which the report's fingerprint is there to check.
    /// </param>
    public static AnalysisReport Apply(
        AnalysisReport report,
        IReadOnlyList<TestMethod> allTests,
        ShardSpec shard,
        int? maxFilterLength = null,
        DurationRecord? durations = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(allTests);
        ArgumentNullException.ThrowIfNull(shard);

        var limit = maxFilterLength ?? DefaultMaxFilterLength;
        var projects = report.Projects;
        var unanalysed = report.UnanalysedTestProjects.Where(p => p.Runs).ToList();

        var byProject = allTests.GroupBy(t => t.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TestMethod>)[.. g.OrderBy(t => t.FullyQualifiedName, StringComparer.Ordinal)], StringComparer.Ordinal);

        var all = projects.Select(p => byProject.GetValueOrDefault(p.Name, [])).ToList();
        var toRun = projects.Select((p, i) => RunSet(p, all[i])).ToList();
        var whole = projects.Select((p, i) => toRun[i].Count == 0).ToList();

        Dictionary<Unit, int> assignment;
        while (true)
        {
            var units = new List<Unit>();
            for (var i = 0; i < projects.Count; i++)
            {
                if (projects[i].SelectedTests == 0 && toRun[i].Count == 0)
                {
                    continue;
                }

                if (whole[i])
                {
                    units.Add(new Unit(i, string.Empty, toRun[i], Weigh(toRun[i], durations)));
                    continue;
                }

                units.AddRange(toRun[i]
                    .GroupBy(ClassCollapser.FullClassName, StringComparer.Ordinal)
                    .Select(g => new Unit(i, g.Key, [.. g], Weigh([.. g], durations))));
            }

            // F# and VB projects that run whole: no tests to count, so each weighs one test.
            for (var i = 0; i < unanalysed.Count; i++)
            {
                units.Add(new Unit(projects.Count + i, unanalysed[i].Name, [], Weigh([], durations)));
            }

            assignment = Deal(units, shard.Count);

            var tooLong = Enumerable.Range(0, projects.Count)
                .Where(i => !whole[i] && !Fits(projects[i], all[i], ShareOf(assignment, i), limit))
                .ToList();

            if (tooLong.Count == 0)
            {
                break;
            }

            foreach (var i in tooLong)
            {
                whole[i] = true;
            }
        }

        var mine = new List<ProjectSelection>();
        for (var i = 0; i < projects.Count; i++)
        {
            var shares = ShareOf(assignment, i);
            if (!shares.TryGetValue(shard.Index - 1, out var tests))
            {
                continue;
            }

            var project = projects[i];
            if (whole[i])
            {
                // As the report had it, filter and all, minus the wave: a wave is a second
                // invocation inside one job, and a sharded job is already a slice.
                mine.Add(project with { FirstWave = null, UnsplitReason = null });
                continue;
            }

            var names = new HashSet<string>(tests.Select(t => t.FullyQualifiedName), StringComparer.Ordinal);
            var filter = Dialect(project).BuildArguments(tests, all[i]);
            mine.Add(project with
            {
                Filtered = true,
                UnfilteredReason = null,
                FilterArguments = filter,
                FilterInFile = FilterPlanner.ArgumentLength(filter) > limit,
                SelectedTests = tests.Count,
                FirstWave = null,
                UnsplitReason = null,
                // In the report's order where it had one, so the nearest tests still lead.
                Tests = project.Filtered
                    ? [.. project.Tests.Where(names.Contains)]
                    : [.. tests.Select(t => t.FullyQualifiedName)],
            });
        }

        var unanalysedHere = report.UnanalysedTestProjects.Select(p =>
        {
            var index = unanalysed.IndexOf(p);
            if (index < 0)
            {
                return p;
            }

            var owner = assignment.First(a => a.Key.Project == projects.Count + index).Value;
            return owner == shard.Index - 1
                ? p
                : p with { Runs = false, Reason = $"runs in shard {owner + 1}/{shard.Count}" };
        }).ToList();

        return report with
        {
            Projects = mine,
            UnanalysedTestProjects = unanalysedHere,
            SelectedTests = mine.Sum(p => p.SelectedTests),
            Shard = new ShardInfo
            {
                Index = shard.Index,
                Count = shard.Count,
                SelectedAcrossShards = report.SelectedTests,
                Weighting = durations is null ? "tests" : "durations",
                DurationsFingerprint = durations?.Fingerprint,
                EstimatedSeconds = durations is null
                    ? null
                    : Math.Round(assignment.Where(a => a.Value == shard.Index - 1).Sum(a => a.Key.Weight), 1),
            },
        };
    }

    /// <summary>A unit's weight: its test count, or what its tests took by the record.</summary>
    private static double Weigh(IReadOnlyList<TestMethod> tests, DurationRecord? durations)
    {
        if (durations is null)
        {
            return Math.Max(1, tests.Count);
        }

        return tests.Count == 0
            ? durations.Typical
            : tests.Sum(t => durations.Seconds.TryGetValue(t.FullyQualifiedName, out var seconds) ? seconds : durations.Typical);
    }

    /// <summary>What the report would run for this project: its selection, or all of it when unfiltered.</summary>
    private static IReadOnlyList<TestMethod> RunSet(ProjectSelection project, IReadOnlyList<TestMethod> all)
    {
        if (!project.Filtered)
        {
            return all;
        }

        var selected = new HashSet<string>(project.Tests, StringComparer.Ordinal);
        return [.. all.Where(t => selected.Contains(t.FullyQualifiedName))];
    }

    /// <summary>Largest first, each to the shard with the least so far; ties to the lowest shard.</summary>
    private static Dictionary<Unit, int> Deal(List<Unit> units, int count)
    {
        var loads = new double[count];
        var result = new Dictionary<Unit, int>();

        foreach (var unit in units
                     .OrderByDescending(u => u.Weight)
                     .ThenBy(u => u.Project)
                     .ThenBy(u => u.Name, StringComparer.Ordinal))
        {
            var target = 0;
            for (var s = 1; s < count; s++)
            {
                if (loads[s] < loads[target])
                {
                    target = s;
                }
            }

            loads[target] += unit.Weight;
            result[unit] = target;
        }

        return result;
    }

    private static Dictionary<int, List<TestMethod>> ShareOf(Dictionary<Unit, int> assignment, int project)
    {
        var shares = new Dictionary<int, List<TestMethod>>();
        foreach (var (unit, shard) in assignment.Where(a => a.Key.Project == project).OrderBy(a => a.Key.Name, StringComparer.Ordinal))
        {
            if (!shares.TryGetValue(shard, out var list))
            {
                list = [];
                shares[shard] = list;
            }

            list.AddRange(unit.Tests);
        }

        return shares;
    }

    /// <summary>
    /// Whether every shard's part of a project can be written as a filter that fits, or failing
    /// that, goes in a file. A project split across one shard only needs no filter beyond the one it
    /// had, but is checked the same way: one rule is easier to trust than two.
    /// </summary>
    private static bool Fits(ProjectSelection project, IReadOnlyList<TestMethod> all, Dictionary<int, List<TestMethod>> shares, int limit)
    {
        var dialect = Dialect(project);
        var runner = TestCommandBuilder.RunnerOf(project);
        foreach (var tests in shares.Values)
        {
            var arguments = dialect.BuildArguments(tests, all);
            if (arguments.Count == 0)
            {
                return false;
            }

            if (FilterPlanner.ArgumentLength(arguments) > limit && !(FilterFiles.Allowed(project) && FilterFiles.CanCarry(runner, arguments)))
            {
                return false;
            }
        }

        return true;
    }

    private static IFilterDialect Dialect(ProjectSelection project) => FilterDialects.For(
        Enum.TryParse<TestFramework>(project.Framework, out var framework) ? framework : TestFramework.Unknown,
        TestCommandBuilder.RunnerOf(project));
}
