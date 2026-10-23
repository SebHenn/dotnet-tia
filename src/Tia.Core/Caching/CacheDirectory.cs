namespace Tia.Core.Caching;

/// <summary>
/// Creates the cache directory, and makes one this tool created ignore itself.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.gitignore</c> holding <c>*</c>, as pytest and mypy leave in theirs. The default
/// <c>.gitignore</c> lists <c>.tia/</c>, but a repository that never added it, or a custom
/// <c>--cache-dir</c>, would otherwise offer the cache to the next <c>git add -A</c>. A committed
/// cache is one the next machine refuses to use (#52), so the commit helps nobody.
/// </para>
/// <para>
/// Only into a directory this call creates. An existing directory may be one the user named with
/// <c>--cache-dir</c> and keeps other files in, and a <c>*</c> there would hide all of them from
/// git.
/// </para>
/// </remarks>
public static class CacheDirectory
{
    /// <summary>
    /// The cache directory as a path below <paramref name="baseDirectory"/>, with forward slashes,
    /// or null when it is not below it: outside the repository, or the base directory itself.
    /// </summary>
    /// <remarks>
    /// What the diff and the clean-tree check leave out, so that a <c>--cache-dir</c> inside the
    /// repository is never a change, as <c>.tia/</c> never was (#44). Null for the base directory
    /// itself, because leaving that out would leave out everything.
    /// </remarks>
    public static string? RelativeTo(string baseDirectory, string repositoryRoot, string cacheDirectory)
    {
        var relative = Path.GetRelativePath(baseDirectory, Path.GetFullPath(cacheDirectory, repositoryRoot));

        return Path.IsPathRooted(relative) || relative is "." or ".." ||
               relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? null
            : relative.Replace('\\', '/');
    }

    public static void Create(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, ".gitignore"), "# Written by dotnet-tia: nothing in its cache belongs in git.\n*\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache works without it; only the protection against committing it is lost.
        }
    }
}
