using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks;

namespace Tia.Core.Tests;

/// <summary>
/// The record behind <c>run --failed-first</c> (#72). It decides an order and nothing else, so the
/// properties that matter are that it never loses a failure it has not seen pass, and that a broken
/// file costs nothing.
/// </summary>
public sealed class LastFailedTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tia-last-failed-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);

    [Fact]
    public void A_test_that_ran_takes_this_runs_outcome()
    {
        var merged = LastFailed.Merge(Set("A", "B"), ran: ["A", "C"], failed: ["C"]);

        Assert.Equal(["C", "B"], merged);
    }

    [Fact]
    public void A_test_that_did_not_run_keeps_its_failure()
    {
        // Not selected this time is not the same as passed.
        var merged = LastFailed.Merge(Set("A"), ran: ["B"], failed: []);

        Assert.Equal(["A"], merged);
    }

    [Fact]
    public void A_failure_in_one_framework_is_not_cleared_by_a_pass_in_another()
    {
        var merged = LastFailed.Merge(Set(), ran: ["A", "A"], failed: ["A"]);

        Assert.Equal(["A"], merged);
    }

    [Fact]
    public void The_cap_drops_the_oldest_failures_first()
    {
        var previous = Set([.. Enumerable.Range(0, LastFailed.Capacity).Select(i => $"Old{i:D4}")]);

        var merged = LastFailed.Merge(previous, ran: ["New"], failed: ["New"]);

        Assert.Equal(LastFailed.Capacity, merged.Count);
        Assert.Equal("New", merged[0]);
    }

    [Fact]
    public void What_is_written_is_read_back()
    {
        LastFailed.Write(_directory, ["App.Tests.A", "App.Tests.B"]);

        Assert.Equal(Set("App.Tests.A", "App.Tests.B"), LastFailed.Read(_directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{ "schemaVersion": 99, "failed": ["A"] }""")]
    public void A_missing_or_unreadable_file_is_no_record_at_all(string content)
    {
        if (content.Length > 0)
        {
            File.WriteAllText(LastFailed.PathIn(_directory), content);
        }

        Assert.Empty(LastFailed.Read(_directory));
    }

    [Theory]
    [InlineData(TestRunner.VsTest, new string[0], true)]
    [InlineData(TestRunner.MicrosoftTestingPlatform, new[] { "Microsoft.Testing.Extensions.TrxReport" }, true)]
    [InlineData(TestRunner.MicrosoftTestingPlatform, new[] { "Microsoft.Testing.Platform" }, false)]
    [InlineData(TestRunner.Unknown, new string[0], false)]
    public void Only_a_runner_that_can_write_trx_is_asked_to(TestRunner runner, string[] references, bool expected)
    {
        Assert.Equal(expected, FrameworkDetector.WritesTrx(runner, references));
    }
}
