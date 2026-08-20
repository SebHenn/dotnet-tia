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

        // The reports this tool prints are not pure ASCII - the replay summary separates its
        // figures with a middle dot, and every percentage carries a non-breaking space. On a
        // console left at an OEM code page those arrive as mojibake in the one output a user is
        // most likely to paste somewhere. Guarded, because setting it throws when stdout is a
        // handle that has no code page to set, and a redirected stream is already UTF-8.
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
        }

        return await BuildRoot(new CommonOptions()).Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The command surface, in one place so a test can assert the real one.
    /// </summary>
    /// <remarks>
    /// It was built inline here, and the test that claimed to check "every command is reachable by
    /// name" constructed its own root from a hand-written list and asserted that. So it could only
    /// ever agree with itself: `shadow` shipped without ever appearing in it, and `stats` would
    /// have done the same.
    /// </remarks>
    public static RootCommand BuildRoot(CommonOptions options) =>
        new("dotnet tia - test impact analysis for .NET. Takes a git diff and runs only the tests it can affect.")
        {
            AnalyzeCommand.Create(options),
            RunCommand.Create(options),
            ExplainCommand.Create(options),
            GraphCommand.Create(options),
            VerifyCommand.Create(options),
            ShadowCommand.Create(options),
            ReplayCommand.Create(options),
            StatsCommand.Create(options),
        };
}
