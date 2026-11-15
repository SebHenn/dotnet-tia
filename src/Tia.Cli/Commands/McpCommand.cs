using System.CommandLine;
using System.Diagnostics;

namespace Tia.Cli.Commands;

/// <summary><c>tia mcp</c>: the MCP server over stdio (#173). See <see cref="McpServer"/>.</summary>
public static class McpCommand
{
    public static Command Create(CommonOptions common)
    {
        var command = new Command("mcp", "Serve selection to coding agents over the Model Context Protocol, on stdio. Start `tia server` for warm answers.");

        command.SetAction(async (_, cancellationToken) =>
        {
            // stdout belongs to the protocol from here on; anything else goes to stderr.
            var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await new McpServer(RunSelf).ServeAsync(Console.In, output, cancellationToken).ConfigureAwait(false);
            return ExitCodes.Ok;
        });

        return command;
    }

    /// <summary>Runs this same tool as a child process, in the directory the agent was started in.</summary>
    private static async Task<TiaInvocation> RunSelf(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable.");
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Started as `dotnet dotnet-tia.dll` rather than through the apphost: pass the assembly on.
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
        {
            start.ArgumentList.Add(entry);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The child process did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new TiaInvocation(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
