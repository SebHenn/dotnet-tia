using System.CommandLine;
using Tia.Workspace;

namespace Tia.Cli;

/// <summary>The options every command shares, defined once so their defaults cannot drift apart.</summary>
public sealed class CommonOptions
{
    public CommonOptions()
    {
        // Both of these decide when a project abandons its filter and runs whole, so an
        // out-of-range value does not fail - it quietly changes how much of the suite executes.
        CoverageThreshold.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<double?>() is < 0 or > 1)
            {
                result.AddError("--coverage-threshold must be a fraction between 0 and 1.");
            }
        });

        MaxFilterLength.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is <= 0)
            {
                result.AddError("--max-filter-length must be greater than zero.");
            }
        });
    }

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

    public Option<string?> CacheDirectory { get; } = new("--cache-dir")
    {
        Description = "Directory holding the cached graph, relative to the repository root. Defaults to .tia.",
    };

    public Option<int?> MaxFilterLength { get; } = new("--max-filter-length")
    {
        Description = "Longest filter argument to emit before a project runs unfiltered. Defaults to the platform's command-line limit.",
    };

    public Option<double?> CoverageThreshold { get; } = new("--coverage-threshold")
    {
        Description = "Fraction of a project's tests above which it runs unfiltered rather than being filtered. Defaults to 0.6.",
    };

    public IEnumerable<Option> All =>
    [
        Base, Path, Solution, Json, Verbose, NoCache, Full, DefaultBranch, NoFallbackFull,
        CacheDirectory, MaxFilterLength, CoverageThreshold,
    ];

    /// <summary>
    /// Adds the shared options to a command, minus any the command would ignore.
    /// </summary>
    /// <remarks>
    /// Every command used to get every option, and three of them read only some. An option that
    /// parses, prints in <c>--help</c> and is then discarded is worse than a missing one: `verify`
    /// accepted <c>--base</c> and mutated the working tree regardless, so the run answered a
    /// different question than the one asked, silently. Omitting them makes the same invocation
    /// fail at the parser with a message.
    /// </remarks>
    public void AddTo(Command command, params Option[] omit)
    {
        foreach (var option in All.Where(o => !omit.Contains(o)))
        {
            command.Options.Add(option);
        }
    }

    /// <remarks>
    /// Reads only what the command was given. <c>GetValue</c> on an option that was scoped off the
    /// command returns its default, so a command that omits <c>--full</c> reads
    /// <c>ForceFull = false</c> and sets what it needs itself.
    /// </remarks>
    public AnalysisOptions Read(ParseResult parseResult, Action<string>? log)
    {
        var path = System.IO.Path.GetFullPath(parseResult.GetValue(Path) ?? Directory.GetCurrentDirectory());

        var options = new AnalysisOptions
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

        if (parseResult.GetValue(CacheDirectory) is { Length: > 0 } cacheDirectory)
        {
            options = options with { CacheDirectory = cacheDirectory };
        }

        if (parseResult.GetValue(MaxFilterLength) is { } maxFilterLength)
        {
            options = options with { MaxFilterLength = maxFilterLength };
        }

        if (parseResult.GetValue(CoverageThreshold) is { } coverageThreshold)
        {
            options = options with { CoverageThreshold = coverageThreshold };
        }

        return options;
    }
}
