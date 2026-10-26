namespace Tia.Integration.Tests;

/// <summary>
/// A submodule bump is one line of the diff, and none of the files inside it are (#124).
/// </summary>
/// <remarks>
/// <c>git diff --name-status</c> reports a moved submodule as its gitlink, the path of the
/// directory it is checked out in. That path matched no project - a project's directory is a
/// prefix of its files, not equal to them - and so a pull request bumping a vendored library ran
/// no tests at all and said nothing.
/// </remarks>
public sealed class SubmoduleTests : IDisposable
{
    private readonly XunitFixtureRepository _repository = new();
    private readonly string _library = Path.Combine(FixtureRepository.RealTempDirectory(), "tia-submodule-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task A_submodule_no_loaded_project_lives_in_runs_everything()
    {
        CreateLibrary(("README.md", "first\n"));
        var previous = AddSubmodule("vendor/lib");
        Commit(("README.md", "second\n"));
        Checkout("vendor/lib", previous, then: "main");

        var report = await _repository.AnalyzeAsync();

        Assert.Equal("full", report.Mode);
        Assert.Contains(report.FullRunReasons, reason => reason.Contains("vendor/lib", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_inside_a_submodule_is_widened_when_it_moves()
    {
        CreateLibrary(
            ("Vendored.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
            ("Greeting.cs", "namespace Vendored; public static class Greeting { public static string Text => \"hello\"; }"));
        var previous = AddSubmodule("vendor/lib");
        _repository.Edit("Fixtures.slnx", "</Solution>", "  <Project Path=\"vendor/lib/Vendored.csproj\" />\n</Solution>");
        TestGit.Run(_repository.Root, "add", "Fixtures.slnx");
        TestGit.Run(_repository.Root, "commit", "-m", "include the vendored project");
        Restore();

        Commit(("Greeting.cs", "namespace Vendored; public static class Greeting { public static string Text => \"hi\"; }"));
        Checkout("vendor/lib", previous, then: "main");

        var report = await _repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, widening =>
            widening.Cause == "Submodule" && widening.Scope.Contains("Vendored", StringComparison.Ordinal));
    }

    private void CreateLibrary(params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(_library);
        TestGit.Init(_library);
        Commit(files);
    }

    private void Commit(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            File.WriteAllText(Path.Combine(_library, path), content);
        }

        TestGit.Run(_library, "add", "-A");
        TestGit.Run(_library, "commit", "-m", "library");
    }

    /// <summary>Adds the library at its current commit, commits that, and returns the commit.</summary>
    private string AddSubmodule(string path)
    {
        // A local path is a file transport, which git refuses for submodules unless allowed.
        TestGit.Run(_repository.Root, "-c", "protocol.file.allow=always", "submodule", "add", _library, path);
        TestGit.Run(_repository.Root, "commit", "-m", "add the submodule");
        return Head(_library);
    }

    /// <summary>Fetches the library's new commit into the checked-out submodule and moves it there.</summary>
    private void Checkout(string path, string committed, string then)
    {
        var submodule = Path.Combine(_repository.Root, path);
        TestGit.Run(submodule, "-c", "protocol.file.allow=always", "fetch", "origin");
        TestGit.Run(submodule, "checkout", "--quiet", "origin/" + then);
        Assert.NotEqual(committed, Head(submodule));
    }

    private void Restore()
    {
        var restored = Tia.Core.Infrastructure.ProcessRunner.Run("dotnet", ["restore", _repository.SolutionPath], _repository.Root);
        Assert.True(restored.Succeeded, restored.StandardError + restored.StandardOutput);
    }

    private static string Head(string repository) =>
        Tia.Core.Infrastructure.ProcessRunner.Run("git", ["rev-parse", "HEAD"], repository).StandardOutput.Trim();

    public void Dispose()
    {
        _repository.Dispose();
        TryDelete(_library);
    }

    private static void TryDelete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        // git marks its objects read-only, which Directory.Delete refuses on Windows.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }
}
