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
    public async Task A_change_inside_a_submodule_is_resolved_to_the_symbols_it_touched()
    {
        // The submodule's own diff names the file and its lines, so the bump is read like any
        // other change, and the vendored project is not widened whole (#154).
        CreateLibrary(
            ("Vendored.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
            ("Greeting.cs", "namespace Vendored;\n\npublic static class Greeting\n{\n    public static string Text => \"hello\";\n\n    public static string Other => \"other\";\n}\n"));
        var previous = AddSubmodule("vendor/lib");
        IncludeVendoredProject();

        Commit(("Greeting.cs", "namespace Vendored;\n\npublic static class Greeting\n{\n    public static string Text => \"hi\";\n\n    public static string Other => \"other\";\n}\n"));
        Checkout("vendor/lib", previous, then: "main");

        var report = await _repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.DoesNotContain(report.Widenings, widening => widening.Cause == "Submodule");
        Assert.Equal(1, report.Diff.ChangedSymbolCount);
        Assert.Contains(report.UntestedChanges, change => change.Symbol.Contains("Greeting.Text", StringComparison.Ordinal));
        Assert.DoesNotContain(report.UntestedChanges, change => change.Symbol.Contains("Greeting.Other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_submodule_whose_old_commit_is_gone_is_widened_whole()
    {
        // As in a shallow submodule clone: the commit the base recorded is not there to diff from,
        // so the projects in it are widened as before.
        CreateLibrary(
            ("Vendored.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
            ("Greeting.cs", "namespace Vendored; public static class Greeting { public static string Text => \"hello\"; }"));
        AddSubmodule("vendor/lib");
        IncludeVendoredProject();

        // Unrelated history, so nothing the submodule keeps still reaches the recorded commit.
        TestGit.Run(_library, "checkout", "--quiet", "--orphan", "rewritten");
        Commit(("Greeting.cs", "namespace Vendored; public static class Greeting { public static string Text => \"hi\"; }"));
        var submodule = Path.Combine(_repository.Root, "vendor", "lib");
        TestGit.Run(submodule, "-c", "protocol.file.allow=always", "fetch", "origin", "rewritten");
        TestGit.Run(submodule, "checkout", "--quiet", "FETCH_HEAD");
        TestGit.Run(submodule, "update-ref", "-d", "refs/remotes/origin/main");
        TestGit.Run(submodule, "branch", "-D", "main");
        TestGit.Run(submodule, "reflog", "expire", "--expire=now", "--all");
        TestGit.Run(submodule, "gc", "--quiet", "--prune=now");

        var report = await _repository.AnalyzeAsync();

        Assert.Contains(report.Widenings, widening =>
            widening.Cause == "Submodule" && widening.Scope.Contains("Vendored", StringComparison.Ordinal));
    }

    private void IncludeVendoredProject()
    {
        _repository.Edit("Fixtures.slnx", "</Solution>", "  <Project Path=\"vendor/lib/Vendored.csproj\" />\n</Solution>");
        TestGit.Run(_repository.Root, "add", "Fixtures.slnx");
        TestGit.Run(_repository.Root, "commit", "-m", "include the vendored project");
        Restore();
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
