using Tia.Cli;
using Tia.Cli.Commands;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary><c>tia init</c> (#176): the workflow it writes, and when it writes nothing.</summary>
public sealed class InitCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        FixtureRepository.RealTempDirectory(), "tia-init-" + Guid.NewGuid().ToString("n"));

    public InitCommandTests()
    {
        WorkspaceLoader.RegisterMSBuild();
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TemporaryDirectory.Delete(_root);

    private static readonly WorkflowChoices Plain = new(
        Solution: "App.slnx", AnalysisPath: null, DefaultBranch: "main", GlobalJson: false, DotnetVersions: ["10.0.x"], Windows: false);

    [Fact]
    public void The_workflow_makes_the_choices_a_first_run_gets_wrong()
    {
        var yaml = WorkflowTemplate.Render(Plain, "0.11.0+abc123");

        Assert.Contains("fetch-depth: 0", yaml, StringComparison.Ordinal);
        Assert.Contains("- run: dotnet restore App.slnx", yaml, StringComparison.Ordinal);
        Assert.Contains("- uses: SebHenn/dotnet-tia@v0.11.0\n", yaml, StringComparison.Ordinal);
        Assert.Contains("branches: [main]", yaml, StringComparison.Ordinal);
        Assert.Contains("runs-on: ubuntu-latest", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/cache", yaml, StringComparison.Ordinal);
        Assert.True(
            yaml.IndexOf("dotnet restore", StringComparison.Ordinal) < yaml.IndexOf("SebHenn/dotnet-tia@", StringComparison.Ordinal),
            "the restore has to come before the analysis");
    }

    [Fact]
    public void A_name_that_is_not_a_plain_yaml_scalar_is_quoted()
    {
        var yaml = WorkflowTemplate.Render(Plain with { Solution = "My App's.sln", DefaultBranch = "release: next" }, "0.11.0");

        Assert.Contains("- run: dotnet restore 'My App''s.sln'", yaml, StringComparison.Ordinal);
        Assert.Contains("branches: ['release: next']", yaml, StringComparison.Ordinal);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Commit()
    {
        TestGit.Init(_root);
        TestGit.Run(_root, "add", "-A");
        TestGit.Run(_root, "commit", "-m", "a repository to set up");
    }

    private string Workflow => Path.Combine(_root, ".github", "workflows", "tia.yml");

    /// <summary>
    /// A runtime per target framework, read from tracked project files only, and a Windows runner
    /// for a project only Windows can build.
    /// </summary>
    [Fact]
    public async Task The_workflow_installs_what_the_projects_target()
    {
        Write("App.slnx", "<Solution />");
        Write("src/App/App.csproj", "<Project><PropertyGroup><TargetFrameworks>net8.0;net48</TargetFrameworks></PropertyGroup></Project>");
        Write("tests/App.Tests/App.Tests.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(".gitignore", "ignored/\n");
        Write("ignored/Old.csproj", "<Project><PropertyGroup><TargetFramework>net5.0</TargetFramework></PropertyGroup></Project>");
        Commit();

        Assert.Equal(ExitCodes.Ok, await Cli.InvokeUnhandledAsync("init", "--path", _root));

        var yaml = await File.ReadAllTextAsync(Workflow, TestContext.Current.CancellationToken);
        Assert.Contains("runs-on: windows-latest", yaml, StringComparison.Ordinal);
        Assert.Contains("            8.0.x\n            10.0.x\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("5.0.x", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("global-json-file", yaml, StringComparison.Ordinal);
        Assert.Contains("solution: App.slnx", yaml, StringComparison.Ordinal);
    }

    /// <summary>A workflow there is someone's, this tool's own included once edited.</summary>
    [Fact]
    public async Task A_workflow_already_there_is_replaced_only_when_asked()
    {
        Write("App.slnx", "<Solution />");
        Write(".github/workflows/tia.yml", "name: mine\n");
        Commit();

        Assert.Equal(ExitCodes.Failed, await Cli.InvokeUnhandledAsync("init", "--path", _root));
        Assert.Equal("name: mine\n", await File.ReadAllTextAsync(Workflow, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.Ok, await Cli.InvokeUnhandledAsync("init", "--path", _root, "--print"));
        Assert.Equal("name: mine\n", await File.ReadAllTextAsync(Workflow, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.Ok, await Cli.InvokeUnhandledAsync("init", "--path", _root, "--force"));
        Assert.StartsWith(WorkflowTemplate.Marker, await File.ReadAllTextAsync(Workflow, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflow goes where GitHub reads it, at the git root, and its paths are relative to the
    /// checkout, so a solution below the root is analysed where it is.
    /// </summary>
    [Fact]
    public async Task A_solution_below_the_git_root_is_named_from_the_root()
    {
        Write("services/api/Api.slnx", "<Solution />");
        Write("global.json", """{ "sdk": { "version": "10.0.100" } }""");
        Commit();

        Assert.Equal(ExitCodes.Ok, await Cli.InvokeUnhandledAsync("init", "--path", Path.Combine(_root, "services", "api")));

        var yaml = await File.ReadAllTextAsync(Workflow, TestContext.Current.CancellationToken);
        Assert.Contains("- run: dotnet restore services/api/Api.slnx", yaml, StringComparison.Ordinal);
        Assert.Contains("args: --path services/api", yaml, StringComparison.Ordinal);
        Assert.Contains("global-json-file: global.json", yaml, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "services", "api", ".github")));
    }

    [Fact]
    public async Task Several_solutions_and_no_choice_write_nothing()
    {
        Write("A.sln", string.Empty);
        Write("B.sln", string.Empty);
        Commit();

        Assert.Equal(ExitCodes.Inconclusive, await Cli.InvokeUnhandledAsync("init", "--path", _root));
        Assert.False(File.Exists(Workflow));
    }
}
