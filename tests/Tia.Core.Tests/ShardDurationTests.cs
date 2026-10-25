using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Validation;
using Tia.Frameworks;

namespace Tia.Core.Tests;

/// <summary>Balancing shards by what tests took rather than by how many there are (#91).</summary>
public sealed class ShardDurationTests
{
    private static TestMethod Test(string className, int method) => new()
    {
        SymbolKey = $"A.Tests|M:App.{className}.T{method}",
        ClassKey = $"A.Tests|T:App.{className}",
        Namespace = "App",
        ClassName = className,
        MethodName = $"T{method}",
        ProjectName = "A.Tests",
        Framework = TestFramework.XUnitV2,
    };

    /// <summary>
    /// The shape tia's own suite has: a few slow tests, and many classes of quick ones. By count the
    /// slow classes are light, so counting deals both to whichever shard is behind - the same one.
    /// </summary>
    private static (List<TestMethod> Suite, DurationRecord Record) SlowAndFast()
    {
        var slow = Enumerable.Range(0, 2).Select(c => Test($"Slow{c}", 0)).ToList();
        var fast = Enumerable.Range(0, 9).SelectMany(c => Enumerable.Range(0, 4).Select(m => Test($"Fast{c}", m))).ToList();

        var seconds = slow.ToDictionary(t => t.FullyQualifiedName, _ => 60.0, StringComparer.Ordinal);
        foreach (var test in fast)
        {
            seconds[test.FullyQualifiedName] = 0.5;
        }

        return ([.. slow, .. fast], new DurationRecord(seconds, TestDurations.Fingerprint(seconds)));
    }

    private static AnalysisReport Full(IReadOnlyList<TestMethod> suite) => new()
    {
        Mode = "full",
        BaseRef = "origin/main",
        TotalTests = suite.Count,
        SelectedTests = suite.Count,
        Projects =
        [
            new ProjectSelection
            {
                Name = "A.Tests", ProjectPath = "/repo/A.Tests.csproj", Framework = "XUnitV2", Runner = "VsTest",
                TotalTests = suite.Count, SelectedTests = suite.Count, Filtered = false, UnfilteredReason = "full run",
            },
        ],
    };

    private static double Time(AnalysisReport shard, DurationRecord record) =>
        shard.Projects.SelectMany(p => p.Tests).Sum(t => record.Seconds[t]);

    [Fact]
    public void A_recorded_split_evens_out_time_where_a_counted_one_does_not()
    {
        var (suite, record) = SlowAndFast();
        var report = Full(suite);

        var byCount = Enumerable.Range(1, 2).Select(i => Time(ShardPlanner.Apply(report, suite, new ShardSpec(i, 2)), record)).ToList();
        var byTime = Enumerable.Range(1, 2).Select(i => Time(ShardPlanner.Apply(report, suite, new ShardSpec(i, 2), durations: record), record)).ToList();

        // Two 60 s tests and 18 s of quick ones. Counted, both slow tests land on one shard;
        // weighed, they part, and the shards differ by at most one quick class.
        Assert.True(byCount.Max() / byCount.Min() > 3, string.Join(", ", byCount));
        Assert.True(byTime.Max() - byTime.Min() <= 2, string.Join(", ", byTime));
    }

    [Fact]
    public void Between_them_the_shards_still_run_everything_once()
    {
        var (suite, record) = SlowAndFast();
        var report = Full(suite);

        var shards = Enumerable.Range(1, 3).Select(i => ShardPlanner.Apply(report, suite, new ShardSpec(i, 3), durations: record)).ToList();

        Assert.Equal(
            suite.Select(t => t.FullyQualifiedName).Order(StringComparer.Ordinal),
            shards.SelectMany(s => s.Projects.SelectMany(p => p.Tests)).Order(StringComparer.Ordinal));
        Assert.All(shards, s => Assert.Equal("durations", s.Shard!.Weighting));
        Assert.All(shards, s => Assert.Equal(record.Fingerprint, s.Shard!.DurationsFingerprint));
        Assert.Equal(record.Seconds.Values.Sum(), shards.Sum(s => s.Shard!.EstimatedSeconds!.Value), 0);
    }

    [Fact]
    public void A_test_with_no_entry_weighs_the_typical_one()
    {
        var (suite, record) = SlowAndFast();
        var partial = new Dictionary<string, double>(record.Seconds, StringComparer.Ordinal);
        partial.Remove(suite[^1].FullyQualifiedName);
        var withGap = new DurationRecord(partial, TestDurations.Fingerprint(partial));

        Assert.Equal(0.5, withGap.Typical);
        var shard = ShardPlanner.Apply(Full(suite), suite, new ShardSpec(1, 1), durations: withGap);
        Assert.Equal(suite.Count, shard.SelectedTests);
    }

    [Fact]
    public void Without_a_record_shards_are_counted_as_before()
    {
        var (suite, _) = SlowAndFast();

        var shard = ShardPlanner.Apply(Full(suite), suite, new ShardSpec(1, 2));

        Assert.Equal("tests", shard.Shard!.Weighting);
        Assert.Null(shard.Shard.DurationsFingerprint);
        Assert.Null(shard.Shard.EstimatedSeconds);
    }

    [Fact]
    public void A_record_moves_halfway_and_keeps_what_did_not_run()
    {
        var merged = TestDurations.Merge(
            new Dictionary<string, double> { ["A"] = 10, ["B"] = 2 },
            new Dictionary<string, double> { ["A"] = 20, ["C"] = 3 });

        Assert.Equal(15, merged["A"]);
        Assert.Equal(2, merged["B"]);
        Assert.Equal(3, merged["C"]);
    }

    [Fact]
    public void A_record_round_trips_and_fingerprints_by_content()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tia-durations-{Guid.NewGuid():n}.json");
        try
        {
            var seconds = new Dictionary<string, double> { ["Ns.B.T"] = 1.25, ["Ns.A.T"] = 0.5 };
            Assert.True(TestDurations.Write(path, seconds));

            var read = TestDurations.Read(path, out var error);
            Assert.Null(error);
            Assert.Equal(seconds.OrderBy(e => e.Key), read!.Seconds.OrderBy(e => e.Key));

            // Line endings are a checkout's business, not the record's.
            File.WriteAllText(path, File.ReadAllText(path).ReplaceLineEndings("\r\n"));
            Assert.Equal(read.Fingerprint, TestDurations.Read(path, out _)!.Fingerprint);
            Assert.NotEqual(read.Fingerprint, TestDurations.Fingerprint(new Dictionary<string, double> { ["Ns.A.T"] = 0.5 }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unreadable_record_says_why()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tia-durations-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Null(TestDurations.Read(path, out var error));
            Assert.Contains("could not be read", error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_trx_result_carries_its_duration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tia-{Guid.NewGuid():n}.trx");
        File.WriteAllText(path, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Works" outcome="Passed" duration="00:00:01.2500000" />
                <UnitTestResult testId="2" testName="Undated" outcome="Passed" />
              </Results>
              <TestDefinitions>
                <UnitTest id="1"><TestMethod className="Ns.Cls" name="Works" /></UnitTest>
                <UnitTest id="2"><TestMethod className="Ns.Cls" name="Undated" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """);
        try
        {
            var results = TrxParser.Parse(path);

            Assert.Equal(1.25, results.Single(r => r.Name == "Ns.Cls.Works").Seconds);
            Assert.Null(results.Single(r => r.Name == "Ns.Cls.Undated").Seconds);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
