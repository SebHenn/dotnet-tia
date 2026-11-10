using Tia.Core.Configuration;
using Tia.Core.Diff;
using Tia.Core.Model;

namespace Tia.Core.Tests;

/// <summary><c>tia.json</c>: its patterns, its parser and what it does to a diff (#170).</summary>
public sealed class ConfigTests
{
    private static PathPattern Path(string text) => PathPattern.Parse(text, out var error) ?? throw new InvalidOperationException(error);

    [Theory]
    [InlineData("db/**", "db/schema.sql", true)]
    [InlineData("db/**", "db/migrations/001.sql", true)]
    [InlineData("db/**", "src/db/schema.sql", false)]
    [InlineData("db", "db/schema.sql", true)]
    [InlineData("db/", "db/schema.sql", true)]
    [InlineData("db", "src/db/schema.sql", true)]
    [InlineData("/db", "src/db/schema.sql", false)]
    [InlineData("docker-compose*.yml", "docker-compose.yml", true)]
    [InlineData("docker-compose*.yml", "deploy/docker-compose.ci.yml", true)]
    [InlineData("*.md", "docs/guide.md", true)]
    [InlineData("*.md", "docs/guide.mdx", false)]
    [InlineData("docs/*.md", "docs/guide.md", true)]
    [InlineData("docs/*.md", "docs/api/guide.md", false)]
    [InlineData("docs/**/*.md", "docs/guide.md", true)]
    [InlineData("docs/**/*.md", "docs/api/guide.md", true)]
    [InlineData("**/Migrations", "src/App/Migrations/001_Init.cs", true)]
    [InlineData("**/Migrations/*.cs", "src/App/Migrations/001_Init.cs", true)]
    [InlineData("src/?pp/*.cs", "src/App/Program.cs", true)]
    [InlineData("src/?pp/*.cs", "src/Appx/Program.cs", false)]
    [InlineData("Docs/**", "docs/guide.md", false)]
    [InlineData("a.b", "aXb", false)]
    [InlineData("db\\**", "db/schema.sql", true)]
    public void A_path_pattern_matches_as_gitignore_would(string pattern, string path, bool matches) =>
        Assert.Equal(matches, Path(pattern).Matches(path));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/")]
    [InlineData("!docs/**")]
    [InlineData("../shared/**")]
    [InlineData("./docs")]
    public void A_path_pattern_that_cannot_mean_what_it_says_is_refused(string pattern) =>
        Assert.Null(PathPattern.Parse(pattern, out _));

    [Theory]
    [InlineData("Web.Tests.SmokeTests", "Web.Tests.SmokeTests.Loads", true)]
    [InlineData("Web.Tests.SmokeTests.Loads", "Web.Tests.SmokeTests.Loads", true)]
    [InlineData("Web.Tests", "Web.Tests.SmokeTests.Loads", true)]
    [InlineData("Web.Tests.Smoke", "Web.Tests.SmokeTests.Loads", false)]
    [InlineData("Web.Tests.Smoke*", "Web.Tests.SmokeTests.Loads", true)]
    [InlineData("*.ContractTests", "Api.Tests.ContractTests.Matches", true)]
    [InlineData("*Contract*", "Api.Tests.OrderContractTests.Matches", true)]
    [InlineData("Api.Tests.Outer", "Api.Tests.Outer+Inner.Runs", true)]
    [InlineData("Api.Tests.Other", "Api.Tests.Outer.Runs", false)]
    public void An_always_run_pattern_names_a_test_or_what_contains_it(string pattern, string name, bool matches) =>
        Assert.Equal(matches, TestNamePattern.Parse(pattern, out _)!.Matches(name));

    [Fact]
    public void A_config_with_comments_and_trailing_commas_is_read()
    {
        var config = TiaConfig.Parse("""
            {
              // smoke tests guard the deploy
              "$schema": "https://example.invalid/tia.schema.json",
              "alwaysRun": ["Web.Tests.SmokeTests",],
              "fullRunPaths": ["db/**", "docker-compose*.yml"],
              "ignorePaths": ["docs/**"],
              "defaults": {
                "solution": "src/App.slnx",
                "base": "origin/develop",
                "defaultBranch": "develop",
                "cacheDir": ".cache/tia",
                "coverageThreshold": 0.8,
                "maxFilterLength": 4000,
              },
            }
            """, "tia.json");

        Assert.Equal(["Web.Tests.SmokeTests"], config.AlwaysRun.Select(p => p.Text));
        Assert.Equal(["db/**", "docker-compose*.yml"], config.FullRunPaths.Select(p => p.Text));
        Assert.Equal(["docs/**"], config.IgnorePaths.Select(p => p.Text));
        Assert.Equal(
            new ConfigDefaults
            {
                Solution = "src/App.slnx",
                Base = "origin/develop",
                DefaultBranch = "develop",
                CacheDir = ".cache/tia",
                CoverageThreshold = 0.8,
                MaxFilterLength = 4000,
            },
            config.Defaults);
    }

    /// <summary>Every one of these would otherwise be read as a rule that quietly does nothing.</summary>
    [Theory]
    [InlineData("""{ "ignorPaths": ["docs/**"] }""", "'ignorPaths' is not a setting")]
    [InlineData("""{ "defaults": { "solutoin": "a.slnx" } }""", "'defaults.solutoin' is not a setting")]
    [InlineData("""{ "ignorePaths": "docs/**" }""", "has to be an array of strings")]
    [InlineData("""{ "ignorePaths": [1] }""", "has to be an array of strings")]
    [InlineData("""{ "ignorePaths": ["!docs/**"] }""", "negation is not supported")]
    [InlineData("""{ "alwaysRun": [""] }""", "is empty")]
    [InlineData("""{ "defaults": { "coverageThreshold": 60 } }""", "a fraction between 0 and 1")]
    [InlineData("""{ "defaults": { "coverageThreshold": "0.6" } }""", "a fraction between 0 and 1")]
    [InlineData("""{ "defaults": { "maxFilterLength": 0 } }""", "greater than zero")]
    [InlineData("""{ "defaults": { "base": "" } }""", "non-empty string")]
    [InlineData("""["docs/**"]""", "has to be a JSON object")]
    [InlineData("""{ "ignorePaths": [ """, "is not valid JSON")]
    public void A_config_that_says_something_it_cannot_mean_is_refused(string json, string expected)
    {
        var refused = Assert.Throws<ConfigFileException>(() => TiaConfig.Parse(json, "tia.json"));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_file_is_no_config()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia-config-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Same(TiaConfig.None, TiaConfig.Load(directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    private static readonly TiaConfig Rules = TiaConfig.Parse("""
        {
          "fullRunPaths": ["db/**"],
          "ignorePaths": ["docs/**", "*.md"]
        }
        """, "tia.json");

    private static DiffResult Diff(params ChangedFile[] files) => new() { BaseRef = "main", BaseCommit = "abc", Files = files };

    private static ChangedFile Modified(string path) => new() { Path = path, Kind = FileChangeKind.Modified };

    [Fact]
    public void An_ignored_file_leaves_the_diff_and_is_listed()
    {
        var configured = ConfigRules.Apply(Rules, string.Empty, Diff(Modified("docs/guide.md"), Modified("src/App/Program.cs")));

        Assert.Equal(["src/App/Program.cs"], configured.Diff.Files.Select(f => f.Path));
        var ignored = Assert.Single(configured.Ignored);
        Assert.Equal(("docs/guide.md", "docs/**"), (ignored.Path, ignored.Pattern));
        Assert.Empty(configured.FullRunReasons);
    }

    [Fact]
    public void A_full_run_path_forces_a_full_run_and_beats_an_ignored_one()
    {
        var configured = ConfigRules.Apply(Rules, string.Empty, Diff(Modified("db/README.md")));

        var reason = Assert.Single(configured.FullRunReasons);
        Assert.Contains("'db/**' in fullRunPaths of tia.json", reason, StringComparison.Ordinal);
        Assert.Empty(configured.Ignored);
    }

    /// <summary>Otherwise a pull request could add a pattern and the files it ignores together.</summary>
    [Fact]
    public void The_config_itself_forces_a_full_run_even_when_a_pattern_would_ignore_it()
    {
        var configured = ConfigRules.Apply(
            TiaConfig.Parse("""{ "ignorePaths": ["*.json"] }""", "tia.json"),
            string.Empty,
            Diff(Modified("tia.json")));

        Assert.Contains("tia.json changed", Assert.Single(configured.FullRunReasons), StringComparison.Ordinal);
        Assert.Empty(configured.Ignored);
    }

    [Fact]
    public void A_file_moved_out_of_an_ignored_directory_is_not_ignored()
    {
        var moved = new ChangedFile { Path = "src/App/Guide.cs", OldPath = "docs/Guide.cs", Kind = FileChangeKind.Renamed };

        var configured = ConfigRules.Apply(Rules, string.Empty, Diff(moved));

        Assert.Equal([moved], configured.Diff.Files);
        Assert.Empty(configured.Ignored);
    }

    [Fact]
    public void A_file_moved_into_a_full_run_path_forces_a_full_run()
    {
        var moved = new ChangedFile { Path = "db/seed.sql", OldPath = "tools/seed.sql", Kind = FileChangeKind.Renamed };

        Assert.Single(ConfigRules.Apply(Rules, string.Empty, Diff(moved)).FullRunReasons);
    }

    /// <summary>
    /// Patterns are relative to the directory holding <c>tia.json</c>, and the diff to the git root.
    /// </summary>
    [Fact]
    public void A_config_below_the_git_root_matches_paths_below_it()
    {
        var configured = ConfigRules.Apply(Rules, "services/api", Diff(
            Modified("services/api/docs/guide.md"),
            Modified("docs/guide.mdx"),
            Modified("services/api/db/schema.sql"),
            Modified("services/api/tia.json")));

        Assert.Equal(["services/api/docs/guide.md"], configured.Ignored.Select(i => i.Path));
        Assert.Equal(2, configured.FullRunReasons.Count);
        Assert.Contains(configured.FullRunReasons, r => r.StartsWith("services/api/tia.json changed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_diff_nothing_matches_is_returned_as_it_was()
    {
        var diff = Diff(Modified("src/App/Program.cs"));

        Assert.Same(diff, ConfigRules.Apply(Rules, string.Empty, diff).Diff);
    }

    private static TestMethod Test(string className, string method) => new()
    {
        SymbolKey = $"M:Api.Tests.{className}.{method}",
        ClassKey = $"T:Api.Tests.{className}",
        Namespace = "Api.Tests",
        ClassName = className,
        MethodName = method,
        ProjectName = "Api.Tests",
        Framework = TestFramework.XUnitV3,
    };

    [Fact]
    public void Always_run_adds_what_the_change_did_not_reach_in_name_order()
    {
        var config = TiaConfig.Parse("""{ "alwaysRun": ["Api.Tests.SmokeTests", "Api.Tests.Gone"] }""", "tia.json");
        TestMethod[] all = [Test("OrderTests", "Totals"), Test("SmokeTests", "Starts"), Test("SmokeTests", "Answers")];

        var added = ConfigRules.AlwaysRun(config, all, [all[1]]);

        Assert.Equal(["Api.Tests.SmokeTests.Answers"], added.Select(t => t.FullyQualifiedName));
        Assert.Equal(["Api.Tests.Gone"], ConfigRules.Unmatched(config, all));
    }
}
