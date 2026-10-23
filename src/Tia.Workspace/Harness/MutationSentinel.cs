using System.Security.Cryptography;
using System.Text.Json;
using Tia.Core.Caching;

namespace Tia.Workspace.Harness;

/// <summary>
/// The harness's refusal to start when it cannot vouch for the working tree it would mutate:
/// <c>git status</c> did not answer, or a mutation left by a killed run has been edited since.
/// </summary>
/// <remarks>
/// Its own type because it is a different answer from the other refusals. A dirty tree or a
/// solution with no tests is a fact about the repository; this is the harness saying it does not
/// know what state the repository is in, which <c>verify</c> reports as inconclusive (exit 2)
/// rather than as a failure.
/// </remarks>
public sealed class UnverifiableWorkingTreeException(string message) : InvalidOperationException(message);

/// <summary>
/// A record, written before each mutation and deleted after its restore, of what the harness is
/// about to change - so that a run killed in between can be undone by the next one.
/// </summary>
/// <remarks>
/// <para>
/// Issue #38. The in-process restore is a <c>finally</c>, and a hard kill - a CI job timeout, a
/// closed terminal, SIGKILL - skips it. The mutated file stays on disk, and the next
/// <c>verify</c> refused on a dirty tree and told the user to commit or stash it: that is, to
/// commit the mutation. <c>docs/benchmarks.md</c> records two killed runs that left one behind.
/// </para>
/// <para>
/// Hashes of both versions, not just the original, because the next run has three cases to tell
/// apart and only one of them is safe to act on. Still the mutated bytes: nobody has touched it,
/// so writing the original back loses nothing. Already the original: the kill landed between this
/// record and the write, or after the restore and before the delete, so only the record is left.
/// Anything else is an edit made since, and putting the original back would destroy it - so the
/// harness refuses and says how to restore it by hand.
/// </para>
/// <para>
/// The original bytes travel inside the record rather than being read back from <c>HEAD</c>.
/// The tree was clean when the run started so the two agree, but only the record still agrees if
/// someone has committed since.
/// </para>
/// </remarks>
internal sealed record MutationSentinel(string RelativePath, string OriginalSha256, string MutatedSha256, string Original)
{
    public const string FileName = "mutation-in-progress.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Where the record lives: in the cache directory, resolved as every other cache reader resolves
    /// it. <c>.tia/</c> is ignored by git and by the diff, so the record is never part of a sample.
    /// </summary>
    public static string Location(string repositoryRoot, string cacheDirectory) =>
        Path.Combine(
            Path.IsPathRooted(cacheDirectory) ? cacheDirectory : Path.Combine(repositoryRoot, cacheDirectory),
            FileName);

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Records a mutation about to be written. Through a temporary file and a move, as the graph
    /// cache is, so a kill during this write leaves either no record or a whole one - and either is
    /// right, because the mutation has not been written yet.
    /// </summary>
    public static void Write(string location, string relativePath, byte[] original, byte[] mutated)
    {
        var sentinel = new MutationSentinel(
            relativePath.Replace('\\', '/'),
            Hash(original),
            Hash(mutated),
            Convert.ToBase64String(original));

        CacheDirectory.Create(Path.GetDirectoryName(location)!);

        var temporary = location + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(sentinel, Json));
        File.Move(temporary, location, overwrite: true);
    }

    /// <summary>Called only once the restore has succeeded: a failed restore keeps its record.</summary>
    public static void Clear(string location) => File.Delete(location);

    /// <summary>
    /// Deals with what a killed run left behind, before anything else looks at the working tree.
    /// </summary>
    /// <returns>A line to print when a file was put back, otherwise null.</returns>
    /// <remarks>
    /// Has to come before the clean-tree check. After it, the leftover mutation is just another
    /// modified file, and the check's advice for one of those is to commit it.
    /// </remarks>
    /// <exception cref="UnverifiableWorkingTreeException">
    /// The file has changed since it was mutated, or the record cannot be read.
    /// </exception>
    public static string? Recover(string repositoryRoot, string cacheDirectory)
    {
        var location = Location(repositoryRoot, cacheDirectory);
        if (!File.Exists(location))
        {
            return null;
        }

        var root = Path.GetFullPath(repositoryRoot);
        var (sentinel, file) = Read(location, root);
        var current = File.Exists(file) ? Hash(File.ReadAllBytes(file)) : "absent";

        if (string.Equals(current, sentinel.MutatedSha256, StringComparison.Ordinal))
        {
            // Byte-for-byte, as the in-process restore does: the file has to go back exactly as
            // the run found it, byte-order mark and line endings included.
            File.WriteAllBytes(file, Convert.FromBase64String(sentinel.Original));
            Clear(location);
            return $"restored {sentinel.RelativePath}, which an interrupted verify run left mutated";
        }

        if (string.Equals(current, sentinel.OriginalSha256, StringComparison.Ordinal))
        {
            Clear(location);
            return null;
        }

        // HEAD named, not left to default: a bare `git checkout -- <path>` restores from the index,
        // which holds the mutation if it has been staged since. Quoted when it has to be, because
        // this is a line to paste and a path with a space would otherwise check out two paths.
        var pathspec = sentinel.RelativePath.Contains(' ', StringComparison.Ordinal)
            ? $"\"{sentinel.RelativePath}\""
            : sentinel.RelativePath;

        throw new UnverifiableWorkingTreeException(
            $"an interrupted verify run mutated {sentinel.RelativePath} and the file has changed since, so the " +
            "harness cannot tell its mutation from your edits and will not overwrite either. To discard " +
            $"both and go back to HEAD, run `git checkout HEAD -- {pathspec}` in {root}. The bytes the run " +
            $"found are in {location} (base64, \"original\"). Delete that file once {sentinel.RelativePath} is as " +
            "it should be.");
    }

    /// <summary>The record, and the file it names, once both are known to be safe to act on.</summary>
    /// <remarks>
    /// <para>
    /// The record is input like any other file in the working tree, not something only this
    /// harness writes. <c>.tia/</c> is ignored by git, but ignoring does not stop a commit from
    /// carrying the file, so a checkout of someone else's branch can bring a record with it. Acting
    /// on a record writes its bytes to the path it names, so the path is held to what the harness
    /// itself would have written there.
    /// </para>
    /// <para>
    /// Inside the repository: a <c>../</c> or a rooted path would write outside it. Not into
    /// <c>.git</c>, which the harness never mutates. Not through a symbolic link, which would lead
    /// out of the repository again. And both hashes are real SHA-256 digests, because the harness
    /// only ever mutates a file that exists - so a missing file, which <see cref="Recover"/> hashes
    /// as <c>absent</c>, can never match a record and be created by it.
    /// </para>
    /// </remarks>
    private static (MutationSentinel Sentinel, string File) Read(string location, string root)
    {
        MutationSentinel? sentinel;

        try
        {
            sentinel = JsonSerializer.Deserialize<MutationSentinel>(File.ReadAllText(location), Json);
        }
        catch (JsonException)
        {
            sentinel = null;
        }

        // The original bytes are checked against their own hash because restoring from a damaged
        // record would write something that was never in the repository - the same harm as the
        // leftover mutation it was meant to undo.
        var file = sentinel is { RelativePath.Length: > 0 } ? Contained(root, sentinel.RelativePath) : null;

        if (sentinel is not { Original: not null } || file is null ||
            !IsDigest(sentinel.OriginalSha256) || !IsDigest(sentinel.MutatedSha256) ||
            !string.Equals(Hash(Decode(sentinel.Original)), sentinel.OriginalSha256, StringComparison.Ordinal))
        {
            throw new UnverifiableWorkingTreeException(
                $"{location} records a mutation from an interrupted verify run, but it cannot be read, so " +
                "the harness does not know which file to restore. Check `git status`, restore any file the " +
                "run mutated with `git checkout HEAD -- <path>`, then delete that record.");
        }

        return (sentinel, file);
    }

    /// <summary>The full path of a file the harness could have mutated, or null for any other path.</summary>
    private static string? Contained(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var file = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;

        // Case-insensitive on Windows and macOS, whose usual file systems are: an ordinal
        // comparison there would turn a containment check into a spelling check.
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!file.StartsWith(prefix, comparison))
        {
            return null;
        }

        var first = file[prefix.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (string.Equals(first, ".git", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(file) && new FileInfo(file).LinkTarget is not null ? null : file;
    }

    private static bool IsDigest(string? hash) =>
        hash is { Length: 64 } && hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static byte[] Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
