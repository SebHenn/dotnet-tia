using Tia.Core.Model;
using Tia.Core.Reporting;

namespace Tia.Cli;

/// <summary>
/// Turns a project's selection into a <c>dotnet test</c> invocation.
/// </summary>
/// <remarks>
/// Where the filter goes depends on the runner, not on the framework: VSTest reads
/// <c>--filter</c> as an argument to <c>dotnet test</c> itself, while Microsoft.Testing.Platform
/// hands everything after <c>--</c> straight to the test executable.
/// </remarks>
public static class TestCommandBuilder
{
    public static IReadOnlyList<string> Build(ProjectSelection project, IReadOnlyList<string> passthrough)
    {
        var arguments = new List<string> { "test", project.ProjectPath };
        arguments.AddRange(passthrough);

        if (!project.Filtered || project.FilterArguments.Count == 0)
        {
            return arguments;
        }

        if (Enum.TryParse<TestRunner>(project.Runner, out var runner) && runner == TestRunner.MicrosoftTestingPlatform)
        {
            arguments.Add("--");
        }

        arguments.AddRange(project.FilterArguments);
        return arguments;
    }

    public static string Describe(IReadOnlyList<string> arguments) =>
        "dotnet " + string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument) =>
        argument.Contains(' ') || argument.Contains('|') ? $"\"{argument}\"" : argument;
}
