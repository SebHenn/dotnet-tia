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
