using System.CommandLine;
using Tia.Workspace;

namespace Tia.Cli;

/// <summary>The options every command shares, defined once so their defaults cannot drift apart.</summary>
public sealed class CommonOptions
{
    public Option<string> Base { get; } = new("--base", "-b")
    {
        Description = "Base revision to diff against.",
        DefaultValueFactory = _ => "origin/main",
    };

    public Option<string> Path { get; } = new("--path", "-p")
    {
        Description = "Repository root. Defaults to the current directory.",
        DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
    };

    public Option<string?> Solution { get; } = new("--solution", "-s")
    {
        Description = "Solution or project to analyse. Discovered from the repository root when omitted.",
    };

    public Option<bool> Json { get; } = new("--json")
    {
        Description = "Emit the full report as JSON on stdout.",
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "List every selected test and log workspace diagnostics.",
    };

    public Option<bool> NoCache { get; } = new("--no-cache")
    {
        Description = "Ignore and do not write .tia/graph-*.bin.",
    };

    public Option<bool> Full { get; } = new("--full")
    {
        Description = "Skip selection and report a full run.",
    };

    public Option<string?> DefaultBranch { get; } = new("--default-branch")
    {
        Description = "Branch that always runs the whole suite. Selection is a pull-request optimisation.",
    };

    public Option<bool> NoFallbackFull { get; } = new("--no-fallback-full-on-error")
    {
        Description = "Fail instead of falling back to a full run when analysis throws.",
    };

    public IEnumerable<Option> All =>
        [Base, Path, Solution, Json, Verbose, NoCache, Full, DefaultBranch, NoFallbackFull];

    public void AddTo(Command command)
    {
        foreach (var option in All)
        {
            command.Options.Add(option);
        }
    }

    public AnalysisOptions Read(ParseResult parseResult, Action<string>? log)
    {
        var path = System.IO.Path.GetFullPath(parseResult.GetValue(Path) ?? Directory.GetCurrentDirectory());

        return new AnalysisOptions
        {
            RepositoryRoot = path,
            BaseRef = parseResult.GetValue(Base) ?? "origin/main",
            SolutionPath = parseResult.GetValue(Solution) is { Length: > 0 } solution
                ? System.IO.Path.GetFullPath(solution)
                : null,
            UseCache = !parseResult.GetValue(NoCache),
            ForceFull = parseResult.GetValue(Full),
            DefaultBranch = parseResult.GetValue(DefaultBranch),
            FallbackFullOnError = !parseResult.GetValue(NoFallbackFull),
            Log = log,
        };
    }
}
