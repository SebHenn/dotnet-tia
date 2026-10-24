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
    public static IReadOnlyList<string> Build(
        ProjectSelection project,
        DotnetTestMode mode,
        IReadOnlyList<string> passthrough,
        IReadOnlyList<string> filterArguments,
        string? trxDirectory = null)
    {
        var arguments = BuildWithoutReport(project, mode, passthrough, filterArguments);

        if (trxDirectory is null || !project.WritesTrx)
        {
            return arguments;
        }

        var runner = Enum.TryParse<TestRunner>(project.Runner, out var parsed) ? parsed : TestRunner.VsTest;
        var withReport = arguments.ToList();

        if (runner != TestRunner.MicrosoftTestingPlatform)
        {
            // A dotnet test option, so before any `--` the caller wrote.
            var separator = withReport.IndexOf("--");
            withReport.InsertRange(separator < 0 ? withReport.Count : separator, ["--logger", "trx", "--results-directory", trxDirectory]);
        }
        else
        {
            // A runner option: straight through on the platform's own dotnet test, and past the
            // separator on the VSTest bridge - adding one when nothing else needed it.
            if (mode == DotnetTestMode.VsTest && !withReport.Contains("--"))
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
        IReadOnlyList<string> filterArguments)
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

        arguments.AddRange(passthrough);

        if (!project.Filtered || filterArguments.Count == 0)
        {
            return arguments;
        }

        var runner = Enum.TryParse<TestRunner>(project.Runner, out var parsed) ? parsed : TestRunner.VsTest;

        if (mode == DotnetTestMode.VsTest && runner == TestRunner.MicrosoftTestingPlatform)
        {
            arguments.Add("--");
        }

        arguments.AddRange(filterArguments);
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
