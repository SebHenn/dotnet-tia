using System.CommandLine;
using Tia.Cli.Commands;
using Tia.Workspace;

namespace Tia.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // MSBuildLocator has to run before anything that touches MSBuild types is JIT-loaded.
        // Doing it first thing in Main is the only placement that reliably holds. A failure is
        // not fatal here - `--help`, `--version` and usage errors need no MSBuild, and this is a
        // global tool, so the machine with only the .NET runtime is exactly the one likely to run
        // `dotnet-tia --help` before installing anything else. The commands that do need it fail
        // with WorkspaceLoader.RegistrationFailure instead of a stack trace from before parsing.
        WorkspaceLoader.RegisterMSBuild();

        var options = new CommonOptions();

        var root = new RootCommand("dotnet tia - test impact analysis for .NET. Takes a git diff and runs only the tests it can affect.")
        {
            AnalyzeCommand.Create(options),
            RunCommand.Create(options),
            ExplainCommand.Create(options),
            GraphCommand.Create(options),
            VerifyCommand.Create(options),
            ShadowCommand.Create(options),
        };

        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }
}
