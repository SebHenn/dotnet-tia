using System.Text.Json;
using Tia.Core.Caching;

namespace Tia.Core.Reporting;

/// <summary>
/// The tests that failed the last time they ran, for <c>run --failed-first</c> (#72).
/// </summary>
/// <remarks>
/// <para>
/// On the same side of the line as the ledger: it decides the order tests run in, never which ones
/// run. A test recorded here that the selection did not pick is not added, and a missing, stale or
/// nonsense file costs the ordering and nothing else - which is also why reading it never fails.
/// </para>
/// <para>
/// A test keeps its entry until it runs again. Merging rather than replacing is what makes this
/// useful across selections: a test that failed on Monday and was not selected on Tuesday is still
/// the likeliest to fail when Wednesday's change reaches it.
/// </para>
/// </remarks>
public static class LastFailed
{
    public const string FileName = "last-failed.json";

    /// <summary>Beyond this the file is a list of a broken suite rather than of last failures.</summary>
    public const int Capacity = 2000;

    private sealed record Document(int SchemaVersion, IReadOnlyList<string> Failed);

    public static string PathIn(string cacheDirectory) => Path.Combine(cacheDirectory, FileName);

    public static IReadOnlySet<string> Read(string cacheDirectory)
    {
        try
        {
            var path = PathIn(cacheDirectory);
            if (!File.Exists(path))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), AnalysisReport.JsonOptions);
            return document is { SchemaVersion: AnalysisReport.SchemaVersion, Failed: { } failed }
                ? new HashSet<string>(failed.Where(f => f.Length > 0), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// What the record becomes after a run: everything that ran takes this run's outcome, and
    /// everything else keeps the one it had.
    /// </summary>
    public static IReadOnlyList<string> Merge(
        IReadOnlySet<string> previous,
        IEnumerable<string> ran,
        IEnumerable<string> failed)
    {
        ArgumentNullException.ThrowIfNull(previous);

        var ranSet = new HashSet<string>(ran, StringComparer.Ordinal);
        var failedNow = failed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var kept = previous.Where(p => !ranSet.Contains(p)).Order(StringComparer.Ordinal);

        // This run's failures first, so the cap drops the oldest news rather than the newest.
        return [.. failedNow.Concat(kept.Where(k => !failedNow.Contains(k))).Take(Capacity)];
    }

    public static void Write(string cacheDirectory, IReadOnlyList<string> failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        try
        {
            CacheDirectory.Create(cacheDirectory);
            var path = PathIn(cacheDirectory);
            AtomicFile.Replace(path, temporary => File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(AnalysisReport.SchemaVersion, failed), AnalysisReport.JsonOptions)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Silent, as the ledger is: the only cost of a lost write is the next run's order.
        }
    }
}
