namespace Tia.Core.Caching;

internal static class AtomicFile
{
    /// <summary>Writes a temporary file beside <paramref name="path"/> and moves it into place.</summary>
    public static void Replace(string path, Action<string> writeTemporary)
    {
        // A name of its own, not `path + ".tmp"`: `watch` and a `run` beside it can save the same
        // cache at once, and two writers sharing one temporary file interleave into a file that
        // neither of them wrote (#44). Each now moves a whole file into place. On Windows a writer
        // can lose the race to replace the file and is refused; it throws, the caller logs that
        // the cache was not written, and the file on disk is the winner's, whole.
        var temporary = $"{path}.{Guid.NewGuid():n}.tmp";
        try
        {
            writeTemporary(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
