using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Tia.Core.Reporting;
using Tia.Frameworks;
using Tia.Workspace;

namespace Tia.Cli;

/// <summary>The options every command shares, defined once so their defaults cannot drift apart.</summary>
public sealed class CommonOptions
{
    /// <remarks>No default value: an absent one is resolved in <see cref="Read"/>, see <see cref="DefaultBase"/>.</remarks>
    public Option<string?> Base { get; } = new("--base", "-b")
    {
        Description = "Base revision to diff against. Defaults to the remote's default branch: origin/HEAD, else origin/main, else origin/master.",
    };

    public Option<string> Path { get; } = new("--path", "-p")
    {
        Description = "Repository root. Defaults to the current directory.",
        DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
    };

    public Option<string?> Solution { get; } = new("--solution", "-s")
    {
        Description = "Solution or project to analyse. Discovered from the repository root when omitted; required when it holds several of one kind.",
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

    public Option<bool> TypeFlow { get; } = new("--type-flow")
    {
        Description = "Bound each interface hop by the concrete types a member can obtain, not merely reach. Experimental; narrows selection at the cost of a second semantic pass.",
    };

    public Option<bool> FailOnUntested { get; } = new("--fail-on-untested")
    {
        Description = "Exit 1 when a changed symbol is reached by no test. Checks every changed symbol, however many.",
    };

    public Option<bool> Annotations { get; } = new("--annotations")
    {
        Description = "Under GitHub Actions, annotate each untested change on the pull request's diff. Does nothing elsewhere.",
    };

    public Option<ShardSpec?> Shard { get; } = new("--shard")
    {
        Description = "Run only part i of n of the selection, such as 2/4, for splitting it across CI jobs. Every shard must analyse the same commit with the same options.",
        CustomParser = result =>
        {
            if (result.Tokens.Count == 0)
            {
                return null;
            }

            var spec = ShardSpec.Parse(result.Tokens[0].Value, out var error);
            if (error is not null)
            {
                result.AddError($"--shard: {error}");
            }

            return spec;
        },
    };

    /// <summary>
    /// This shard's part of a report, or the report unchanged without <c>--shard</c>. A load that
    /// resolved nothing is left alone: there is nothing to divide, and the refusal has to survive.
    /// </summary>
    public AnalysisReport ApplyShard(ParseResult parseResult, AnalysisOutcome outcome, AnalysisOptions options)
    {
        if (parseResult.GetValue(Shard) is not { } shard || outcome.Report.WorkspaceUnusable)
        {
            return outcome.Report;
        }

        DurationRecord? durations = null;
        if (parseResult.GetValue(ShardDurations) is { Length: > 0 } path)
        {
            // Refused rather than ignored: a shard that quietly fell back to counting would divide
            // differently from shards that read the record, and some classes would run twice and
            // others never.
            durations = TestDurations.Read(System.IO.Path.GetFullPath(path), out var error)
                ?? throw new SetupException($"--shard-durations: {error} Every shard has to divide by the same record, so none is guessed.");
        }

        return ShardPlanner.Apply(outcome.Report, outcome.AllTests, shard, options.MaxFilterLength, durations);
    }

    public Option<string?> ShardDurations { get; } = new("--shard-durations")
    {
        Description = "With --shard: balance shards by the test durations in this file (`run --record-durations` writes .tia/durations.json). Give every shard the same file.",
    };

    public Option<bool> NoServer { get; } = new("--no-server")
    {
        Description = "Analyse in this process even when a `dotnet tia server` is running here.",
    };

    /// <summary>
    /// The analysis, from a running <c>tia server</c> when there is one for this repository and
    /// solution, and in this process otherwise (#76). Either way it is the same analysis.
    /// </summary>
    public Task<AnalysisOutcome> AnalyzeAsync(
        ParseResult parseResult,
        AnalysisOptions options,
        Action<string>? verbose,
        CancellationToken cancellationToken) =>
        ServedOrLocalAsync(
            parseResult,
            options,
            withSolution => Server.ServerClient.TryAnalyzeAsync(withSolution, verbose, cancellationToken),
            outcome => outcome,
            cancellationToken);

    /// <summary>
    /// Asks a running <c>tia server</c> when there is one here, and otherwise analyses in this
    /// process and answers from that.
    /// </summary>
    public async Task<T> ServedOrLocalAsync<T>(
        ParseResult parseResult,
        AnalysisOptions options,
        Func<AnalysisOptions, Task<T?>> served,
        Func<AnalysisOutcome, T> local,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!parseResult.GetValue(NoServer) && File.Exists(Server.ServerProtocol.StatePath(options.ResolvedCacheDirectory)))
        {
            // Settled here rather than left to discovery, because the server is keyed by the
            // solution. A refusal is the one discovery would have raised anyway.
            var withSolution = options with
            {
                SolutionPath = SetupException.RequireSolution(options.RepositoryRoot, options.SolutionPath),
            };

            if (await served(withSolution).ConfigureAwait(false) is { } answer)
            {
                return answer;
            }
        }

        return local(await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false));
    }

    public Option<bool> ReportSkipped { get; } = new("--report-skipped")
    {
        Description = "List the tests this selection leaves out, per project, in the JSON report, so a later failure of one can be recognised as a selection miss.",
    };

    /// <summary>
    /// The JSON report, written to a file. <c>run</c> has no <c>--json</c>, because its stdout belongs
    /// to the test runners, so this is how its selection reaches a later job (#75).
    /// </summary>
    public Option<string?> ReportFile { get; } = new("--report")
    {
        Description = "Also write the JSON report to this file.",
    };

    public void WriteReport(ParseResult parseResult, AnalysisReport report)
    {
        if (parseResult.GetValue(ReportFile) is { Length: > 0 } path)
        {
            File.WriteAllText(path, report.ToJson() + Environment.NewLine, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public Option<string?> Summary { get; } = new("--summary")
    {
        Description = "Append a Markdown summary of the selection to this file, such as $GITHUB_STEP_SUMMARY.",
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
    internal static T? ParseBounded<T>(
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
        TypeFlow, CacheDirectory, MaxFilterLength, CoverageThreshold,
    ];

    /// <summary>
    /// The options of the commands that report a selection, <c>analyze</c> and <c>run</c>, which add
    /// them after their own. Not in <see cref="All"/>: no other command takes them, except that
    /// <c>explain</c> adds <see cref="NoServer"/> alone.
    /// </summary>
    public IEnumerable<Option> Selection =>
    [
        FailOnUntested, Annotations, Summary, Shard, ShardDurations, ReportSkipped, ReportFile, NoServer,
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

        // Resolved only for a command that takes --base; the others set their own and should not
        // pay for three git processes to be told so.
        var resolved = parseResult.GetValue(Base) is { Length: > 0 } given
            ? new ResolvedBase(given, null)
            : parseResult.CommandResult.Command.Options.Contains(Base)
                ? DefaultBase.Resolve(path)
                : new ResolvedBase("origin/main", null);

        var options = new AnalysisOptions
        {
            RepositoryRoot = path,
            BaseRef = resolved.Ref,
            BaseRefHint = resolved.Hint,
            SolutionPath = parseResult.GetValue(Solution) is { Length: > 0 } solution
                ? System.IO.Path.GetFullPath(solution)
                : null,
            UseCache = !parseResult.GetValue(NoCache),
            ForceFull = parseResult.GetValue(Full),
            DefaultBranch = parseResult.GetValue(DefaultBranch),
            FallbackFullOnError = !parseResult.GetValue(NoFallbackFull),
            TypeFlow = parseResult.GetValue(TypeFlow),
            CheckEveryChange = parseResult.GetValue(FailOnUntested),
            ReportSkipped = parseResult.GetValue(ReportSkipped),
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
