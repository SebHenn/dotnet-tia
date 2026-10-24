using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks;

namespace Tia.Core.Tests;

/// <summary>
/// Splitting a run across CI jobs (#73). The property that matters: between them the shards run
/// exactly what one job would have, each test in exactly one of them, and every job reaches that
/// answer on its own.
/// </summary>
public sealed class ShardPlannerTests
{
    private static TestMethod Test(string project, string className, int method) => new()
    {
        SymbolKey = $"{project}|M:App.{className}.T{method}",
        ClassKey = $"{project}|T:App.{className}",
        Namespace = "App",
        ClassName = className,
        MethodName = $"T{method}",
        ProjectName = project,
        Framework = TestFramework.XUnitV2,
    };

    /// <summary>Two projects of uneven classes, the way real suites are.</summary>
    private static List<TestMethod> Suite() =>
    [
        .. Enumerable.Range(0, 7).SelectMany(c => Enumerable.Range(0, 1 + (c * 3 % 5)).Select(m => Test("A.Tests", $"A{c}Tests", m))),
        .. Enumerable.Range(0, 4).SelectMany(c => Enumerable.Range(0, 2 + c).Select(m => Test("B.Tests", $"B{c}Tests", m))),
    ];

    private static ProjectSelection Project(string name, IReadOnlyList<TestMethod> all, IReadOnlyList<TestMethod>? selected) => new()
    {
        Name = name,
        ProjectPath = $"/repo/{name}/{name}.csproj",
        Framework = nameof(TestFramework.XUnitV2),
        Runner = nameof(TestRunner.VsTest),
        TotalTests = all.Count,
        SelectedTests = selected?.Count ?? all.Count,
        Filtered = selected is not null,
        UnfilteredReason = selected is null ? "full run" : null,
        FilterArguments = selected is null ? [] : ["--filter", "x"],
        Tests = selected is null ? [] : [.. selected.Select(t => t.FullyQualifiedName)],
    };

    private static AnalysisReport Report(IReadOnlyList<TestMethod> suite, Func<TestMethod, bool>? selected = null)
    {
        var projects = suite.GroupBy(t => t.ProjectName)
            .Select(g => Project(g.Key, [.. g], selected is null ? null : [.. g.Where(selected)]))
            .ToList();

        return new AnalysisReport
        {
            Mode = selected is null ? "full" : "selective",
            BaseRef = "origin/main",
            TotalTests = suite.Count,
            SelectedTests = projects.Sum(p => p.SelectedTests),
            Projects = projects,
        };
    }

    /// <summary>What a shard runs: its filtered tests, or every test of a project it runs whole.</summary>
    private static List<string> Runs(AnalysisReport shard, IReadOnlyList<TestMethod> suite) =>
    [
        .. shard.Projects.SelectMany(p => p.Filtered
            ? p.Tests
            : suite.Where(t => t.ProjectName == p.Name).Select(t => t.FullyQualifiedName)),
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(40)]
    public void The_shards_of_a_full_run_cover_it_exactly_once(int count)
    {
        var suite = Suite();
        var report = Report(suite);

        var runs = Enumerable.Range(1, count)
            .SelectMany(i => Runs(ShardPlanner.Apply(report, suite, new ShardSpec(i, count)), suite))
            .ToList();

        Assert.Equal(suite.Count, runs.Count);
        Assert.Equal(suite.Select(t => t.FullyQualifiedName).Order(), runs.Order());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void The_shards_of_a_selection_cover_it_exactly_once(int count)
    {
        var suite = Suite();
        var report = Report(suite, t => t.MethodName != "T1");
        var expected = report.Projects.SelectMany(p => p.Tests).Order().ToList();

        var shards = Enumerable.Range(1, count).Select(i => ShardPlanner.Apply(report, suite, new ShardSpec(i, count))).ToList();
        var runs = shards.SelectMany(s => Runs(s, suite)).ToList();

        Assert.Equal(expected, runs.Order());
        Assert.All(shards, s => Assert.Equal(report.SelectedTests, s.Shard!.SelectedAcrossShards));
        Assert.Equal(report.SelectedTests, shards.Sum(s => s.SelectedTests));
    }

    [Fact]
    public void Every_job_reaches_the_same_split_whatever_order_discovery_returned()
    {
        var suite = Suite();
        var shuffled = suite.OrderBy(t => t.FullyQualifiedName.GetHashCode(StringComparison.Ordinal) ^ 0x5bd1e995).ToList();
        var report = Report(suite);

        for (var i = 1; i <= 3; i++)
        {
            var spec = new ShardSpec(i, 3);
            Assert.Equal(
                Runs(ShardPlanner.Apply(report, suite, spec), suite),
                Runs(ShardPlanner.Apply(report, shuffled, spec), suite));
        }
    }

    [Fact]
    public void A_class_is_never_split_across_shards()
    {
        var suite = Suite();
        var report = Report(suite);

        var owners = Enumerable.Range(1, 3)
            .SelectMany(i => Runs(ShardPlanner.Apply(report, suite, new ShardSpec(i, 3)), suite).Select(name => (Shard: i, Class: name[..name.LastIndexOf('.')])))
            .GroupBy(x => x.Class)
            .Select(g => g.Select(x => x.Shard).Distinct().Count());

        Assert.All(owners, shards => Assert.Equal(1, shards));
    }

    [Fact]
    public void The_shards_are_balanced_to_within_the_largest_class()
    {
        var suite = Suite();
        var report = Report(suite);
        var largest = suite.GroupBy(t => t.ClassKey).Max(g => g.Count());

        var sizes = Enumerable.Range(1, 3).Select(i => ShardPlanner.Apply(report, suite, new ShardSpec(i, 3)).SelectedTests).ToList();

        Assert.True(sizes.Max() - sizes.Min() <= largest, string.Join(", ", sizes));
    }

    [Fact]
    public void A_project_whose_share_cannot_be_written_as_a_filter_runs_whole_in_one_shard()
    {
        var suite = Suite();
        var report = Report(suite);

        // Too short for any class filter, so both projects go whole.
        var shards = Enumerable.Range(1, 2).Select(i => ShardPlanner.Apply(report, suite, new ShardSpec(i, 2), maxFilterLength: 10)).ToList();

        Assert.All(shards.SelectMany(s => s.Projects), p => Assert.False(p.Filtered));
        Assert.Equal(["A.Tests", "B.Tests"], shards.SelectMany(s => s.Projects).Select(p => p.Name).Order());
        Assert.Equal(suite.Count, shards.Sum(s => Runs(s, suite).Count));
    }

    [Fact]
    public void An_unanalysed_project_runs_in_exactly_one_shard()
    {
        var suite = Suite();
        var report = Report(suite) with
        {
            UnanalysedTestProjects =
            [
                new UnanalysedTestProject { Name = "F.Tests", Language = "F#", ProjectPath = "/repo/F.Tests.fsproj", Runs = true, Reason = "full run" },
            ],
        };

        var running = Enumerable.Range(1, 3)
            .Select(i => ShardPlanner.Apply(report, suite, new ShardSpec(i, 3)))
            .Count(s => s.UnanalysedTestProjects.Single().Runs);

        Assert.Equal(1, running);
    }

    [Theory]
    [InlineData("1/1", 1, 1)]
    [InlineData("2/4", 2, 4)]
    public void A_shard_is_written_i_of_n(string text, int index, int count)
    {
        Assert.Equal(new ShardSpec(index, count), ShardSpec.Parse(text, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0/4")]
    [InlineData("5/4")]
    [InlineData("2")]
    [InlineData("a/b")]
    [InlineData("-1/2")]
    [InlineData("1/0")]
    public void Anything_else_is_refused(string text)
    {
        Assert.Null(ShardSpec.Parse(text, out var error));
        Assert.NotNull(error);
    }
}
