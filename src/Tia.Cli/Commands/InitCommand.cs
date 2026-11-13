using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tia.Core.Infrastructure;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>What a repository's workflow needs that <c>tia init</c> can read off it.</summary>
/// <param name="Solution">The solution or project to restore and analyse, relative to the git root.</param>
/// <param name="AnalysisPath">The directory to analyse, relative to the git root, or null for the root.</param>
/// <param name="DefaultBranch">The branch a push to which runs the workflow and runs everything.</param>
/// <param name="GlobalJson">Whether the git root has a <c>global.json</c> to install the SDK from.</param>
/// <param name="DotnetVersions">Every <c>N.0.x</c> the projects target, so their tests have a runtime.</param>
/// <param name="Windows">Whether any project needs Windows: .NET Framework, WPF, Windows Forms or a <c>-windows</c> target.</param>
public sealed record WorkflowChoices(
    string Solution,
    string? AnalysisPath,
    string DefaultBranch,
    bool GlobalJson,
    IReadOnlyList<string> DotnetVersions,
    bool Windows);

/// <summary>
/// The GitHub Actions workflow <c>tia init</c> writes (#176): the recipe in <c>docs/usage.md</c>
/// with the choices a first-time user gets wrong already made.
/// </summary>
/// <remarks>
/// <para>
/// The choices, and why each one is made for the user:
/// a full clone, because from a shallow one the base is unreachable and every run is a full one;
/// a restore before the action, because an unrestored project does not compile and that is also a
/// full run; the action pinned to the release that wrote this, because a release tag here never
/// moves; and a push to the default branch running everything, which the action does by itself.
/// </para>
/// <para>
/// No cache of <c>.tia/</c>. Restoring one from the base branch is safe only when nothing that
/// runs there executes unreviewed code, and nothing here can check that. Without it the graph is
/// built on every run, which costs time and never a test; the comment in the file says how to add
/// one and what to check first.
/// </para>
/// </remarks>
public static class WorkflowTemplate
{
    public const string Marker = "# Written by `dotnet tia init`";

    public static string Render(WorkflowChoices choices, string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(toolVersion);

        var version = toolVersion.Split('+')[0];
        var yaml = new StringBuilder();

        void Line(string text = "") => yaml.Append(text).Append('\n');

        Line($"{Marker} (dotnet-tia {version}). It is yours now: tia never rewrites it.");
        Line("# What it does: https://github.com/SebHenn/dotnet-tia/blob/main/docs/usage.md#ci");
        Line("name: tests");
        Line();
        Line("on:");
        Line("  pull_request:");
        Line("  push:");
        Line($"    branches: [{Scalar(choices.DefaultBranch)}]");
        Line();
        Line("permissions:");
        Line("  contents: read");
        Line();
        Line("jobs:");
        Line("  test:");
        Line($"    runs-on: {(choices.Windows ? "windows-latest" : "ubuntu-latest")}");
        Line("    steps:");
        Line("      # The whole history: tia diffs against the pull request's base, and a shallow clone");
        Line("      # cannot reach it, so every run would be a full one.");
        Line("      - uses: actions/checkout@v7");
        Line("        with:");
        Line("          fetch-depth: 0");
        Line();
        Line("      - uses: actions/setup-dotnet@v6");
        Line("        with:");
        if (choices.GlobalJson)
        {
            Line("          global-json-file: global.json");
        }

        if (choices.DotnetVersions.Count > 0)
        {
            Line("          # A runtime for every target framework the projects name, so each test project can run.");
            Line("          dotnet-version: |");
            foreach (var dotnet in choices.DotnetVersions)
            {
                Line($"            {dotnet}");
            }
        }
        else if (!choices.GlobalJson)
        {
            Line("          dotnet-version: '10.0.x'");
        }

        Line();
        Line("      # Before the analysis: a project that has not been restored does not compile, and tia");
        Line("      # runs everything rather than reason about it.");
        Line($"      - run: dotnet restore {Scalar(choices.Solution)}");
        Line();
        Line("      # Selects the tests this change can affect and runs them, and writes what it chose and");
        Line($"      # why to the job summary. On {choices.DefaultBranch}, and whenever it cannot tell, it runs everything.");
        Line($"      - uses: SebHenn/dotnet-tia@v{version}");
        Line("        with:");
        Line($"          solution: {Scalar(choices.Solution)}");
        Line("          configuration: Release");
        if (choices.AnalysisPath is { } path)
        {
            Line($"          args: --path {Scalar(path)}");
        }

        Line();
        Line("      # No cache of .tia/ here on purpose. Caching it against the base branch makes the analysis");
        Line("      # faster, but a pull request restores whatever any job on that branch saved, and a planted");
        Line("      # graph selects too few tests. Add one only if nothing that runs on the base branch executes");
        Line("      # code you have not reviewed: https://github.com/SebHenn/dotnet-tia#caching");

        return yaml.ToString();
    }

    /// <summary>A YAML plain scalar when the text is safe as one, single-quoted otherwise.</summary>
    private static string Scalar(string text) =>
        Regex.IsMatch(text, "^[A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            ? text
            : "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>Reads <see cref="WorkflowChoices"/> off a repository.</summary>
public static class RepositorySurvey
{
    private static readonly Regex TargetFrameworks = new(
        "<TargetFrameworks?[^>]*>([^<]*)</TargetFrameworks?>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Modern = new(
        @"^net(?<major>\d+)\.\d+(?<windows>-windows.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Framework = new(
        @"^net[2-4]\d*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <param name="gitRoot">Where the workflow goes, and what its paths are relative to.</param>
    /// <param name="analysisRoot">The directory <c>--path</c> named.</param>
    /// <param name="solution">The solution to analyse, discovered or given, as a full path.</param>
    public static WorkflowChoices Read(string gitRoot, string analysisRoot, string solution)
    {
        var relative = Path.GetRelativePath(gitRoot, analysisRoot).Replace('\\', '/');
        var majors = new SortedSet<int>();
        var windows = false;

        // Tracked files only: what CI checks out, without bin/ and obj/.
        var listed = ProcessRunner.Run(
            "git",
            ["ls-files", "-z", "--", "*.csproj", "*.fsproj", "*.vbproj", "*.props"],
            gitRoot,
            TimeSpan.FromSeconds(30));

        foreach (var file in listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(gitRoot, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read names no framework; the workflow still installs the
                // SDK, and the first run says which runtime is missing.
                continue;
            }

            windows |= text.Contains("<UseWPF>true", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("<UseWindowsForms>true", StringComparison.OrdinalIgnoreCase);

            foreach (Match match in TargetFrameworks.Matches(text))
            {
                foreach (var framework in match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Modern.Match(framework) is { Success: true } modern &&
                        int.Parse(modern.Groups["major"].Value, CultureInfo.InvariantCulture) is >= 5 and var major)
                    {
                        majors.Add(major);
                        windows |= modern.Groups["windows"].Success;
                    }
                    else if (Framework.IsMatch(framework))
                    {
                        windows = true;
                    }
                }
            }
        }

        var branch = DefaultBase.Resolve(gitRoot).Ref;
        return new WorkflowChoices(
            Solution: Path.GetRelativePath(gitRoot, solution).Replace('\\', '/'),
            AnalysisPath: relative == "." ? null : relative,
            DefaultBranch: branch.StartsWith("origin/", StringComparison.Ordinal) ? branch["origin/".Length..] : branch,
            GlobalJson: File.Exists(Path.Combine(gitRoot, "global.json")),
            DotnetVersions: [.. majors.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m}.0.x"))],
            Windows: windows);
    }
}

/// <summary><c>tia init</c> (#176): a CI workflow with the choices already made.</summary>
public static class InitCommand
{
    public const string WorkflowPath = ".github/workflows/tia.yml";

    public static Command Create(CommonOptions common)
    {
        var force = new Option<bool>("--force") { Description = $"Replace {WorkflowPath} if it is already there." };
        var print = new Option<bool>("--print") { Description = "Print the workflow instead of writing it." };

        var command = new Command("init", $"Write a GitHub Actions workflow, {WorkflowPath}, that runs the selected tests on every pull request.");
        command.Options.Add(common.Path);
        command.Options.Add(common.Solution);
        command.Options.Add(force);
        command.Options.Add(print);

        command.SetAction(SolutionChoice.Refusing((parseResult, _) =>
        {
            var options = common.Read(parseResult, null);
            var git = SetupException.RequireRepository(options.RepositoryRoot);
            var gitRoot = Path.GetFullPath(git.RepositoryRoot);
            var solution = SetupException.RequireSolution(options.RepositoryRoot, options.SolutionPath);

            var yaml = WorkflowTemplate.Render(
                RepositorySurvey.Read(gitRoot, options.RepositoryRoot, solution),
                Server.ServerProtocol.ToolVersion);

            if (parseResult.GetValue(print))
            {
                Console.Out.Write(yaml);
                return Task.FromResult(ExitCodes.Ok);
            }

            var path = Path.Combine(gitRoot, WorkflowPath.Replace('/', Path.DirectorySeparatorChar));
            var replacing = File.Exists(path);
            if (replacing && !parseResult.GetValue(force))
            {
                // Any workflow there is someone's, this tool's own included once it has been
                // edited, so it is never replaced without being asked.
                Console.Error.WriteLine($"  {WorkflowPath} is already there and is left alone. --force replaces it; --print shows what would.");
                return Task.FromResult(ExitCodes.Failed);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Console.Out.WriteLine($"  {(replacing ? "Replaced" : "Wrote")} {WorkflowPath}. Commit it, and the next pull request runs only the tests it can affect.");
            Console.Out.WriteLine("  Before that, `dotnet tia replay --commits 20` shows what selection would have done on your recent history.");
            Console.Out.WriteLine("  A tia.json can name tests to always run and paths that run everything: see docs/usage.md#tiajson.");
            return Task.FromResult(ExitCodes.Ok);
        }));

        return command;
    }
}
