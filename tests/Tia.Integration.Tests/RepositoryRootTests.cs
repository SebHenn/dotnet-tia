using Tia.Core.Diff;
using Tia.Core.Infrastructure;

namespace Tia.Integration.Tests;

/// <summary>
/// Which spelling of the repository root the engine works in.
/// </summary>
/// <remarks>
/// <para>
/// <c>git rev-parse --show-toplevel</c> resolves symlinks. MSBuild keeps whatever path it was
/// handed. Every diff path is combined with the former and compared against documents named by the
/// latter, so under a symlinked checkout they never met: no document matched, no symbol was seeded,
/// and the run selected nothing.
/// </para>
/// <para>
/// Nothing reported a problem, because no single step had failed - the diff resolved, the workspace
/// loaded, and zero files matched zero documents. <c>run</c> printed "nothing was impacted by this
/// diff" and exited 0. That is a silent miss reported as success, which is the one outcome the
/// safety model exists to make impossible, and reaching it took nothing more exotic than analysing
/// a repository through a symlink.
/// </para>
/// <para>
/// macOS reaches it by default: <c>Path.GetTempPath()</c> is under <c>/var</c>, a symlink to
/// <c>/private/var</c>, which is why its CI leg failed every selection test while Linux passed.
/// </para>
/// </remarks>
public sealed class RepositoryRootTests
{
    [Fact]
    public void A_repository_reached_through_a_symlink_is_rooted_where_the_caller_looked()
    {
        using var repository = TemporaryRepository.Create();
        if (repository.Link is null)
        {
            Assert.Skip("this platform will not create a symbolic link without elevation");
        }

        var git = GitClient.Discover(repository.Link);

        Assert.NotNull(git);

        // Not the resolved target: a root the caller could not have named is a root nothing the
        // caller loaded will match.
        Assert.Equal(Path.GetFullPath(repository.Link), git.RepositoryRoot);
    }

    [Fact]
    public void A_subdirectory_still_resolves_to_the_root_above_it()
    {
        using var repository = TemporaryRepository.Create();
        if (repository.Link is null)
        {
            Assert.Skip("this platform will not create a symbolic link without elevation");
        }

        // The root is found by ascending as far as git says the caller is deep, so a caller below
        // the root exercises that arithmetic rather than the empty case.
        var nested = Path.Combine(repository.Link, "src", "nested");
        Directory.CreateDirectory(nested);

        var git = GitClient.Discover(nested);

        Assert.NotNull(git);
        Assert.Equal(Path.GetFullPath(repository.Link), git.RepositoryRoot);
    }

    [Fact]
    public void The_fixture_temp_directory_is_a_real_directory_and_not_the_filesystem_root()
    {
        // GetTempPath ends with a separator and GetFileName of such a path is empty, so an earlier
        // version of this resolution collected no segments and returned "/" or "C:\". Linux refused
        // to create a fixture there and failed; an elevated Windows runner created one and passed.
        // A helper that returns the filesystem root must never look like a working answer again.
        var temp = FixtureRepository.RealTempDirectory();

        Assert.True(Directory.Exists(temp), $"'{temp}' is not a directory");
        Assert.NotEqual(Path.GetPathRoot(temp), temp);

        // And it still has to be the temp directory, not merely some directory that exists.
        Assert.Equal(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))),
            Path.GetFileName(temp));
    }

    /// <summary>A git repository in a temporary directory, plus a symlink pointing at it.</summary>
    private sealed class TemporaryRepository : IDisposable
    {
        private TemporaryRepository(string real, string? link)
        {
            Real = real;
            Link = link;
        }

        public string Real { get; }

        /// <summary>Null when this platform refuses to create one without elevation - Windows
        /// outside Developer Mode - which is a skip rather than a failure.</summary>
        public string? Link { get; }

        public static TemporaryRepository Create()
        {
            var real = Path.Combine(Path.GetTempPath(), "tia-root-real-" + Guid.NewGuid().ToString("n"));
            var link = Path.Combine(Path.GetTempPath(), "tia-root-link-" + Guid.NewGuid().ToString("n"));

            Directory.CreateDirectory(real);
            ProcessRunner.Run("git", ["init", "--initial-branch=main"], real);

            try
            {
                Directory.CreateSymbolicLink(link, real);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TemporaryRepository(real, null);
            }

            return new TemporaryRepository(real, link);
        }

        public void Dispose()
        {
            if (Link is not null)
            {
                // Deletes the link, never what it points at.
                try
                {
                    Directory.Delete(Link);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            // git's loose objects are read-only, and Windows will not delete a read-only file.
            try
            {
                foreach (var file in Directory.EnumerateFiles(Real, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(Real, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
