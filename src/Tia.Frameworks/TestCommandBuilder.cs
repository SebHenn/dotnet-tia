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
        IReadOnlyList<string> passthrough)
    {
        var arguments = new List<string> { "test" };

        if (mode == DotnetTestMode.MicrosoftTestingPlatform)
        {
            arguments.Add("--project");
        }

        arguments.Add(project.ProjectPath);
        arguments.AddRange(passthrough);

        if (!project.Filtered || project.FilterArguments.Count == 0)
        {
            return arguments;
        }

        var runner = Enum.TryParse<TestRunner>(project.Runner, out var parsed) ? parsed : TestRunner.VsTest;

        if (mode == DotnetTestMode.VsTest && runner == TestRunner.MicrosoftTestingPlatform)
        {
            arguments.Add("--");
        }

        arguments.AddRange(project.FilterArguments);
        return arguments;
    }

    public static DotnetTestMode ModeOf(AnalysisReport report) =>
        Enum.TryParse<DotnetTestMode>(report.DotnetTestMode, out var mode) ? mode : DotnetTestMode.VsTest;

    public static string Describe(IReadOnlyList<string> arguments) =>
        "dotnet " + string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument) =>
        argument.Contains(' ', StringComparison.Ordinal) || argument.Contains('|', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
}
