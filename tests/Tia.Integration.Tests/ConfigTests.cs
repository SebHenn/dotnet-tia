using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary>The fixture solution with a committed <c>tia.json</c> (#170).</summary>
public sealed class ConfigFixture : IDisposable
{
    public XunitFixtureRepository Repository { get; } = new();

    public ConfigFixture()
    {
        Write("tia.json", """
            {
              // The fixture's own rules: one test class that always runs, one directory that runs
              // everything, and text files that select nothing.
              "alwaysRun": ["Fixtures.Tests.SplitterTests", "Fixtures.Tests.NoSuchTests"],
              "fullRunPaths": ["db/**"],
              "ignorePaths": ["*.txt"],
              "defaults": { "base": "HEAD" }
            }
            """);
        Write("Fixtures.Core/notes.txt", "notes\n");
        Write("db/schema.sql", "create table t (id int);\n");

        TestGit.Run(Repository.Root, "add", "-A");
        TestGit.Run(Repository.Root, "commit", "-m", "a tia.json");
    }

    private void Write(string relativePath, string content)
    {
        var path = Repository.PathOf(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => Repository.Dispose();
}

public sealed class ConfigTests(ConfigFixture fixture) : IClassFixture<ConfigFixture>, IDisposable
{
    private XunitFixtureRepository Repository => fixture.Repository;

    public void Dispose()
    {
        Repository.Revert();
        TestGit.Run(Repository.Root, "reset", "-q", "--hard");
    }

    private static readonly string[] AlwaysRun =
    [
        "Fixtures.Tests.SplitterTests.Takes_the_first",
        "Fixtures.Tests.SplitterTests.Takes_the_last",
    ];

    /// <summary>
    /// A file in a project's directory widens that project, and the core project is referenced by
    /// every test project, so without the config this edit selects the whole suite.
    /// </summary>
    [Fact]
    public async Task An_ignored_file_selects_nothing_and_is_reported()
    {
        Repository.Write("Fixtures.Core/notes.txt", "more notes\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("selective", report.Mode);
        Assert.Equal("tia.json", report.Config);
        var ignored = Assert.Single(report.IgnoredFiles);
        Assert.Equal(("Fixtures.Core/notes.txt", "*.txt"), (ignored.Path, ignored.Pattern));
        Assert.DoesNotContain(report.Widenings, w => w.Cause == "ContentFile");
        Assert.Equal(0, report.Diff.FileCount);
        Assert.Equal(AlwaysRun, FixtureRepository.SelectedTests(report));
    }

    [Fact]
    public async Task Always_run_tests_follow_the_tests_the_change_reached()
    {
        Repository.Edit("Fixtures.Core/Calculator.cs", "a + b", "b + a");

        var report = await Repository.AnalyzeAsync();

        var tests = Assert.Single(report.Projects, p => p.Name == "Fixtures.Tests").Tests;
        Assert.Equal(["Fixtures.Tests.CalculatorTests.Adds", .. AlwaysRun], tests.Where(t => !t.Contains("Reflective", StringComparison.Ordinal)));
        Assert.Contains(report.Widenings, w => w is { Cause: "AlwaysRun", Scope: "Fixtures.Tests" } && w.Detail.StartsWith("2 test(s)", StringComparison.Ordinal));
        Assert.Contains(report.Diagnostics, d => d.Contains("'Fixtures.Tests.NoSuchTests' in alwaysRun of tia.json names no test", StringComparison.Ordinal));
        Assert.Empty(report.IgnoredFiles);
    }

    [Fact]
    public async Task A_full_run_path_runs_everything()
    {
        Repository.Write("db/schema.sql", "create table t (id bigint);\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("full", report.Mode);
        Assert.Contains(report.FullRunReasons, r => r.Contains("db/schema.sql changed - it matches 'db/**' in fullRunPaths of tia.json", StringComparison.Ordinal));
    }

    /// <summary>The safe answer when the two disagree: an ignored file that also forces a full run forces it.</summary>
    [Fact]
    public async Task A_full_run_path_wins_over_an_ignored_one()
    {
        Repository.Write("db/readme.txt", "how the schema evolves\n");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("full", report.Mode);
        Assert.Empty(report.IgnoredFiles);
    }

    [Fact]
    public async Task Changing_the_config_runs_everything()
    {
        Repository.Edit("tia.json", "\"ignorePaths\": [\"*.txt\"]", "\"ignorePaths\": [\"*.txt\", \"*.cs\"]");
        Repository.Edit("Fixtures.Core/Calculator.cs", "a + b", "b + a");

        var report = await Repository.AnalyzeAsync();

        Assert.Equal("full", report.Mode);
        Assert.Contains(report.FullRunReasons, r => r.StartsWith("tia.json changed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The fixture has no remote, so without <c>defaults.base</c> the base would not resolve and
    /// the run would be a full one.
    /// </summary>
    [Fact]
    public async Task A_default_from_the_config_stands_in_for_a_missing_flag()
    {
        Repository.Edit("Fixtures.Core/Calculator.cs", "a + b", "b + a");
        var path = Path.Combine(FixtureRepository.RealTempDirectory(), "tia-report-" + Guid.NewGuid().ToString("n") + ".json");

        try
        {
            Assert.Equal(0, await Cli.InvokeAsync("analyze", "--path", Repository.AnalysisRoot, "--no-cache", "--report", path));
            var report = AnalysisReport.FromJson(File.ReadAllText(path))!;

            Assert.Equal("HEAD", report.BaseRef);
            Assert.Equal("selective", report.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_flag_on_the_command_line_wins_over_the_config()
    {
        var path = Path.Combine(FixtureRepository.RealTempDirectory(), "tia-report-" + Guid.NewGuid().ToString("n") + ".json");

        try
        {
            await Cli.InvokeAsync("analyze", "--path", Repository.AnalysisRoot, "--no-cache", "--base", "HEAD~1", "--report", path);
            var report = AnalysisReport.FromJson(File.ReadAllText(path))!;

            Assert.Equal("HEAD~1", report.BaseRef);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
