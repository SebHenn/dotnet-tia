using System.Text.Json.Nodes;
using Tia.Core.Configuration;
using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class BaselineTests
{
    private static Finding F(string key, int line = 1) => new("src/A.cs", line, "method", key, "P", key);

    private static DeadCodeReport Report(params Finding[] findings) => new(1, 10, 2, 1, 0, findings);

    [Fact]
    public void A_finding_the_baseline_holds_is_dropped_even_when_its_line_moved()
    {
        var baseline = Baseline.From(Report(F("A.Old()", line: 4)));

        var (report, accepted) = baseline.Apply(Report(F("A.Old()", line: 40), F("A.New()")));

        Assert.Equal(["A.New()"], report.Findings.Select(f => f.Key));
        Assert.Equal(1, accepted);
    }

    [Fact]
    public void A_baseline_survives_a_round_trip_through_its_file()
    {
        var written = Baseline.From(Report(F("B()"), F("A()"), F("A()")));

        var read = Baseline.Parse(written.ToJson(), "baseline.json");

        Assert.Equal(["A()", "B()"], read.Keys);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 9, \"keys\": [] }")]
    [InlineData("{ \"schemaVersion\": 1, \"keys\": [], \"extra\": 1 }")]
    [InlineData("not json")]
    public void A_file_that_is_not_a_baseline_this_version_reads_is_refused(string json)
    {
        Assert.Throws<ConfigFileException>(() => Baseline.Parse(json, "baseline.json"));
    }

    [Fact]
    public void Sarif_carries_the_file_the_line_and_a_fingerprint_that_survives_line_moves()
    {
        var log = JsonNode.Parse(Sarif.Write(Report(F("A.Old()", line: 7))))!;

        var result = log["runs"]![0]!["results"]![0]!;
        Assert.Equal("2.1.0", (string?)log["version"]);
        Assert.Equal("src/A.cs", (string?)result["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"]);
        Assert.Equal(7, (int?)result["locations"]![0]!["physicalLocation"]!["region"]!["startLine"]);
        Assert.Equal("A.Old()", (string?)result["partialFingerprints"]!["symbolKey/v1"]);
    }
}
