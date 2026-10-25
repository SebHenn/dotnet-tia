using Tia.Core.Model;
using Tia.Core.Reporting;

namespace Tia.Frameworks;

/// <summary>
/// Turns a project's selection into a <c>dotnet test</c> invocation.
/// </summary>
/// <remarks>
/// Three shapes, and picking the wrong one produces a command that fails outright rather than one
/// that runs the wrong tests:
/// <list type="bullet">
/// <item>VSTest: <c>dotnet test &lt;project&gt; --filter "…"</c>.</item>
/// <item>Microsoft.Testing.Platform over the VSTest bridge: the project is still positional, but
/// runner arguments have to cross a <c>--</c> separator to reach the test executable.</item>
/// <item>The platform-native <c>dotnet test</c>, opted into through <c>global.json</c>: the
/// project moves to <c>--project</c> and runner arguments are passed directly, with no
/// separator.</item>
/// </list>
/// </remarks>
public static class TestCommandBuilder
{
    public static IReadOnlyList<string> Build(
        ProjectSelection project,
        DotnetTestMode mode,
        IReadOnlyList<string> passthrough) =>
        Build(project, mode, passthrough, project?.FilterArguments ?? []);

    /// <param name="filterArguments">
    /// The filter to use in place of the project's own, which is how one wave of a divided run gets
    /// invoked. Where the arguments go - before <c>--</c>, after it, or straight through - is a
    /// property of the runner and stays the same whichever wave is being built.
    /// </param>
    /// <param name="trxDirectory">
    /// Where to write a TRX report, for <c>run --failed-first</c> (#72), or null for none. Ignored
    /// for a project that cannot write one without failing the run.
    /// </param>
    /// <param name="filterFiles">
    /// Where to write the filter of a project whose filter is too long for the command line
    /// (<see cref="ProjectSelection.FilterInFile"/>, #90). Required for such a project: without it
    /// the command would fail to start, or on another platform run with a filter no runner was
    /// meant to see inline.
    /// </param>
    public static IReadOnlyList<string> Build(
        ProjectSelection project,
        DotnetTestMode mode,
        IReadOnlyList<string> passthrough,
        IReadOnlyList<string> filterArguments,
        string? trxDirectory = null,
        FilterFiles? filterFiles = null)
    {
        var arguments = BuildWithoutReport(project, mode, passthrough, filterArguments, filterFiles);

        if (trxDirectory is null || !project.WritesTrx)
        {
            return arguments;
        }

        var runner = Enum.TryParse<TestRunner>(project.Runner, out var parsed) ? parsed : TestRunner.VsTest;
        var withReport = arguments.ToList();

        if (runner != TestRunner.MicrosoftTestingPlatform || mode == DotnetTestMode.MicrosoftTestingPlatform)
        {
            // A dotnet test option on VSTest, and an option the platform's own dotnet test passes
            // straight through: before any `--` either way. Past one, the platform command reads
            // the existing results directory as a positional argument naming a directory, and
            // refuses to run - which a filter passed in a file puts there (#90).
            var separator = withReport.IndexOf("--");
            var report = runner == TestRunner.MicrosoftTestingPlatform
                ? ["--report-trx", "--results-directory", trxDirectory]
                : new[] { "--logger", "trx", "--results-directory", trxDirectory };
            withReport.InsertRange(separator < 0 ? withReport.Count : separator, report);
        }
        else
        {
            // A runner option past the separator on the VSTest bridge, adding one when nothing
            // else needed it.
            if (!withReport.Contains("--"))
            {
                withReport.Add("--");
            }

            withReport.AddRange(["--report-trx", "--results-directory", trxDirectory]);
        }

        return withReport;
    }

    private static IReadOnlyList<string> BuildWithoutReport(
        ProjectSelection project,
        DotnetTestMode mode,
        IReadOnlyList<string> passthrough,
        IReadOnlyList<string> filterArguments,
        FilterFiles? filterFiles)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(filterArguments);

        var arguments = new List<string> { "test" };

        if (mode == DotnetTestMode.MicrosoftTestingPlatform)
        {
            arguments.Add("--project");
        }

        arguments.Add(project.ProjectPath);

        // One instance of a multi-targeted project. Without it `dotnet test` runs every framework
        // the project declares, and with an entry per framework that ran each of them once per
        // entry (#67). A framework the caller chose wins, and is not contradicted.
        if (project.TargetFramework is { } framework && !NamesFramework(passthrough))
        {
            arguments.AddRange(["--framework", framework]);
        }

        // The caller's arguments in two halves: options for `dotnet test`, and after their `--`,
        // arguments for whatever runs the tests - inline RunSettings on VSTest, runner options on
        // the bridge. The filter goes in the half it belongs to. Appending it after the whole
        // passthrough put it on the far side of the caller's `--`, where VSTest read `--filter` as
        // a RunSettings entry and the bridge was handed a second `--` (#79).
        var separator = passthrough.ToList().IndexOf("--");
        var forDotnetTest = separator < 0 ? passthrough : passthrough.Take(separator).ToList();
        var forRunner = separator < 0 ? [] : passthrough.Skip(separator + 1).ToList();

        arguments.AddRange(forDotnetTest);

        var filtered = project.Filtered && filterArguments.Count > 0;
        var runner = Enum.TryParse<TestRunner>(project.Runner, out var parsed) ? parsed : TestRunner.VsTest;
        var filterForRunner = mode == DotnetTestMode.VsTest && runner == TestRunner.MicrosoftTestingPlatform;

        if (filtered && project.FilterInFile)
        {
            if (filterFiles is null)
            {
                throw new InvalidOperationException(
                    $"{project.Name}'s filter is too long for the command line and has to be passed in a file, but no file was provided for it.");
            }

            filterArguments = filterFiles.Write(runner, filterArguments);

            // Past the separator on either platform command, because only the test application
            // reads a response file without expanding it first: `dotnet test` would expand one it
            // saw, and start the application with the very command line the file was to avoid.
            filterForRunner = runner == TestRunner.MicrosoftTestingPlatform;
        }

        if (filtered && !filterForRunner)
        {
            arguments.AddRange(filterArguments);
        }

        var runnerArguments = new List<string>(forRunner);
        if (filtered && filterForRunner)
        {
            runnerArguments.AddRange(filterArguments);
        }

        if (runnerArguments.Count > 0 || separator >= 0)
        {
            arguments.Add("--");
            arguments.AddRange(runnerArguments);
        }

        return arguments;
    }

    /// <summary>
    /// The selections to invoke, with the instances of a multi-targeted project folded into one
    /// wherever they selected the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One invocation without <c>--framework</c> runs a project's frameworks side by side, which is
    /// what <c>dotnet test</c> does unasked and is the cheapest way to run them all: NodaTime's
    /// main suite takes about 24 s that way, 42 s one framework after another, and 49 s the way it
    /// used to run, every framework once per instance (#67). So instances whose filters, waves and
    /// filtering agree are run as one, in the position of the first.
    /// </para>
    /// <para>
    /// That one invocation also runs a framework the selection skipped, when only some instances
    /// were selected and those agree. It runs more than was selected, never less. Instances that
    /// disagree each keep their own <c>--framework</c>.
    /// </para>
    /// <para>
    /// Only for <c>run</c> and <c>watch</c>. The harnesses read each project's results under its own
    /// name, and folding two instances there would report one framework's failures as the other's.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ProjectSelection> Coalesce(IReadOnlyList<ProjectSelection> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var groups = projects
            .GroupBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<ProjectSelection>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            var instances = groups[project.ProjectPath];

            if (instances.Count < 2 || instances.Any(i => i.TargetFramework is null) || !instances.All(i => SameRun(i, project)))
            {
                result.Add(project);
                continue;
            }

            if (emitted.Add(project.ProjectPath))
            {
                result.Add(project with { TargetFramework = null });
            }
        }

        return result;
    }

    private static bool SameRun(ProjectSelection a, ProjectSelection b) =>
        a.Filtered == b.Filtered &&
        a.FilterInFile == b.FilterInFile &&
        a.FilterArguments.SequenceEqual(b.FilterArguments, StringComparer.Ordinal) &&
        (a.FirstWave, b.FirstWave) switch
        {
            (null, null) => true,
            ({ } x, { } y) => x.TestCount == y.TestCount &&
                              x.FilterArguments.SequenceEqual(y.FilterArguments, StringComparer.Ordinal) &&
                              x.RemainderFilterArguments.SequenceEqual(y.RemainderFilterArguments, StringComparer.Ordinal),
            _ => false,
        };

    /// <summary>
    /// Whether the caller's arguments already choose a framework. Only those before a <c>--</c>
    /// reach <c>dotnet test</c> itself; anything after belongs to the test runner.
    /// </summary>
    private static bool NamesFramework(IReadOnlyList<string> passthrough) =>
        passthrough.TakeWhile(a => a != "--").Any(a =>
            a is "-f" or "--framework" ||
            a.StartsWith("--framework=", StringComparison.Ordinal) ||
            a.StartsWith("--framework:", StringComparison.Ordinal) ||
            a.StartsWith("-f:", StringComparison.Ordinal) ||
            a.StartsWith("-f=", StringComparison.Ordinal));

    public static DotnetTestMode ModeOf(AnalysisReport report) =>
        Enum.TryParse<DotnetTestMode>(report.DotnetTestMode, out var mode) ? mode : DotnetTestMode.VsTest;

    public static string Describe(IReadOnlyList<string> arguments) =>
        "dotnet " + string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument) =>
        argument.Contains(' ', StringComparison.Ordinal) || argument.Contains('|', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
}
