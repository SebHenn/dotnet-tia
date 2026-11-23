using System.CommandLine;
using Tia.DeadCode.Cli.Commands;
using Tia.Workspace;

namespace Tia.DeadCode.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Before anything that touches MSBuild types is JIT-loaded, and not fatal when it fails:
        // the same placement and the same reasoning as Tia.Cli's Main.
        WorkspaceLoader.RegisterMSBuild();

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
        }

        try
        {
            return await BuildRoot().Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // MSBuild fails in ways no command anticipates - a global.json that pins an SDK this
            // machine lacks - and a crash would read as "findings" or as nothing to a script that
            // only checks the code. It is reported, and it is the inconclusive code: no answer.
            Console.Error.WriteLine($"Nothing can be concluded: {ex.GetType().Name}: {ex.Message}");
            return ExitCodes.Inconclusive;
        }
    }

    /// <summary>The command surface, in one place so a test can assert the real one.</summary>
    public static RootCommand BuildRoot() =>
        new("Finds code, package references and files a .NET solution no longer uses.")
        {
            ScanCommand.Create(),
            OracleCommand.Create(),
            ReferencesCommand.Create(),
        };
}
