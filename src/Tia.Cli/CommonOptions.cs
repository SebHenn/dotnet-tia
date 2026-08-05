using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
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

    public Option<string?> CacheDirectory { get; } = new("--cache-dir")
    {
        Description = "Directory holding the cached graph, relative to the repository root. Defaults to .tia.",
    };

    public Option<int?> MaxFilterLength { get; } = new("--max-filter-length")
    {
        Description = "Longest filter argument to emit before a project runs unfiltered. Defaults to the platform's command-line limit.",
        CustomParser = result => ParseBounded<int>(
            result,
            "--max-filter-length",
            token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null,
            value => value > 0,
            "a whole number greater than zero"),
    };

    /// <remarks>See <see cref="ParseBounded{T}"/> for why this does not use the built-in conversion.</remarks>
    public Option<double?> CoverageThreshold { get; } = new("--coverage-threshold")
    {
        Description = "Fraction of a project's tests above which it runs unfiltered rather than being filtered. Defaults to 0.6.",
        CustomParser = result => ParseBounded<double>(
            result,
            "--coverage-threshold",
            token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null,
            value => value is >= 0 and <= 1,
            "a fraction between 0 and 1, written with a decimal point as in 0.6"),
    };

    /// <summary>
    /// Reads and range-checks a numeric option in one place, invariantly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves of this were defects. The conversion was left to the built-in parser, which uses
    /// the *current* culture, so <c>--coverage-threshold 0.6</c> read as <c>6</c> on any machine
    /// whose decimal separator is a comma and was then rejected as not being a fraction between 0
    /// and 1 - which is precisely what had been typed. A command-line argument is not
    /// culture-sensitive text.
    /// </para>
    /// <para>
    /// The range check then lived in a validator that called <c>GetValueOrDefault</c>, which
    /// *rethrows* a failed conversion. So a non-numeric value did not produce a parse error: it
    /// escaped the parser as an unhandled <c>InvalidOperationException</c> and printed a stack
    /// trace at the user. Parsing and range-checking in the same place is what stops the two from
    /// disagreeing about whether a value exists.
    /// </para>
    /// </remarks>
    private static T? ParseBounded<T>(
        ArgumentResult result,
        string optionName,
        Func<string, T?> parse,
        Func<T, bool> inRange,
        string expectation)
        where T : struct
    {
        if (result.Tokens.Count == 0)
        {
            return null;
        }

        var token = result.Tokens[0].Value;

        if (parse(token) is not { } value)
        {
            result.AddError($"{optionName}: '{token}' is not a number. Expected {expectation}.");
            return null;
        }

        if (!inRange(value))
        {
            result.AddError($"{optionName}: {token} is out of range. Expected {expectation}.");
            return null;
        }

        return value;
    }

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
