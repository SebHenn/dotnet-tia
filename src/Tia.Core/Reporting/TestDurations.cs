using System.Globalization;
using System.Text;
using System.Text.Json;
using Tia.Core.Caching;
using Tia.Core.Infrastructure;

namespace Tia.Core.Reporting;

/// <summary>What each test took, from the runs that recorded it (#91).</summary>
/// <param name="Fingerprint">
/// Of the content rather than the file's bytes, so a record committed through a checkout that
/// rewrites line endings fingerprints the same on every platform. Shards compare it to know they
/// split by the same record.
/// </param>
public sealed record DurationRecord(IReadOnlyDictionary<string, double> Seconds, string Fingerprint)
{
    /// <summary>What a test with no entry is taken to cost: the median entry, or one second in an empty record.</summary>
    public double Typical { get; } = Median(Seconds.Values);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 1.0 : sorted[sorted.Count / 2];
    }
}

/// <summary>
/// The per-test durations <c>run --record-durations</c> keeps, which <c>--shard-durations</c>
/// balances shards by (#91).
/// </summary>
/// <remarks>
/// <para>
/// Per test rather than per class, because a selective run runs part of a class, and a class
/// total learned from part of it would understate the class every time.
/// </para>
/// <para>
/// A moving average, so one slow run on a busy machine moves an entry halfway rather than all the
/// way. Capped, safe to delete, and never read implicitly by a shard: every shard has to divide by
/// the same record or they disagree about who runs what, so the record is a file the caller names.
/// </para>
/// </remarks>
public static class TestDurations
{
    public const string FileName = "durations.json";

    public const int Capacity = 50_000;

    private sealed record Document(int SchemaVersion, IReadOnlyDictionary<string, double> Seconds);

    public static string PathIn(string cacheDirectory) => Path.Combine(cacheDirectory, FileName);

    /// <summary>The record at <paramref name="path"/>, or null with the reason when it cannot be read.</summary>
    public static DurationRecord? Read(string path, out string? error)
    {
        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), AnalysisReport.JsonOptions);
            if (document is not { SchemaVersion: AnalysisReport.SchemaVersion, Seconds: { } seconds })
            {
                error = $"{path} is not a durations record of schemaVersion {AnalysisReport.SchemaVersion}.";
                return null;
            }

            var valid = seconds
                .Where(e => e.Key.Length > 0 && double.IsFinite(e.Value) && e.Value >= 0)
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

            error = null;
            return new DurationRecord(valid, Fingerprint(valid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"{path} could not be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The record after a run: each test that ran moves halfway to what it took this time, a new
    /// one takes what it took, and the rest keep theirs.
    /// </summary>
    public static Dictionary<string, double> Merge(IReadOnlyDictionary<string, double> previous, IReadOnlyDictionary<string, double> observed)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(observed);

        var merged = new Dictionary<string, double>(previous, StringComparer.Ordinal);
        foreach (var (test, seconds) in observed)
        {
            merged[test] = merged.TryGetValue(test, out var before) ? (before + seconds) / 2 : seconds;
        }

        if (merged.Count <= Capacity)
        {
            return merged;
        }

        // Over the cap: what this run observed stays, then the rest in name order, so the trim is
        // the same however often it is repeated.
        return merged
            .OrderBy(e => observed.ContainsKey(e.Key) ? 0 : 1)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(Capacity)
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }

    /// <summary>Writes the record whole to a temporary file and moves it into place. Never throws.</summary>
    public static bool Write(string path, IReadOnlyDictionary<string, double> seconds)
    {
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } directory)
            {
                CacheDirectory.Create(directory);
            }

            var sorted = new SortedDictionary<string, double>(seconds.ToDictionary(e => e.Key, e => Math.Round(e.Value, 4)), StringComparer.Ordinal);
            AtomicFile.Replace(path, temporary => File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(AnalysisReport.SchemaVersion, sorted), AnalysisReport.JsonOptions)));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Fingerprint(IReadOnlyDictionary<string, double> seconds)
    {
        ArgumentNullException.ThrowIfNull(seconds);

        var text = new StringBuilder();
        foreach (var (test, value) in seconds.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            text.Append(test).Append('\t').Append(Math.Round(value, 4).ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        return Hashing.Sha256Hex(text.ToString(), 12);
    }
}
