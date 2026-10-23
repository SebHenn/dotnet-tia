using Tia.Core.Caching;

namespace Tia.Core.Tests;

/// <summary>
/// A cache directory this tool creates ignores itself, and one it did not create is left alone
/// (#52).
/// </summary>
public sealed class CacheDirectoryTests : IDisposable
{
    private readonly DirectoryInfo _parent = Directory.CreateTempSubdirectory("tia-cache-dir-");

    [Fact]
    public void A_directory_it_creates_gets_a_gitignore_that_ignores_everything()
    {
        var directory = Path.Combine(_parent.FullName, ".tia");

        CacheDirectory.Create(directory);

        var lines = File.ReadAllLines(Path.Combine(directory, ".gitignore"));
        Assert.Contains("*", lines);
    }

    /// <summary>
    /// A <c>--cache-dir</c> can name a directory the user keeps other files in, and a <c>*</c>
    /// there would hide all of them from git.
    /// </summary>
    [Fact]
    public void An_existing_directory_is_not_given_one()
    {
        var directory = Path.Combine(_parent.FullName, "build");
        Directory.CreateDirectory(directory);

        CacheDirectory.Create(directory);

        Assert.False(File.Exists(Path.Combine(directory, ".gitignore")));
    }

    public void Dispose() => _parent.Delete(recursive: true);
}
