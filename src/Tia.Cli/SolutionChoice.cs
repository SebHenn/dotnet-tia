using System.CommandLine;
using Tia.Workspace;

namespace Tia.Cli;

/// <summary>
/// How every command answers when it has nothing it can analyse: several solutions and no choice
/// between them, none at all, or a <c>--path</c> that does not exist or is not in a repository.
/// </summary>
/// <remarks>
/// <para>
/// One wrapper rather than a catch in each command, because the answer has to be the same
/// everywhere and the commands reach discovery by different roads: <c>analyze</c>, <c>graph</c>
/// and <c>explain</c> through the analyzer, <c>shadow</c> and <c>verify</c> through a harness that
/// runs one, <c>run</c> before it starts its build, <c>watch</c> directly. A catch written into
/// each would be seven chances for one to say 1, or to print a stack trace (issues #37, #50).
/// </para>
/// <para>
/// Exit 2, not 1. Nothing was analysed, nothing ran and nothing failed; the command could not
/// conclude anything, which is what <see cref="ExitCodes.Inconclusive"/> means everywhere else.
/// </para>
/// </remarks>
internal static class SolutionChoice
{
    public static Func<ParseResult, CancellationToken, Task<int>> Refusing(
        Func<ParseResult, CancellationToken, Task<int>> action) =>
        async (parseResult, cancellationToken) =>
        {
            try
            {
                return await action(parseResult, cancellationToken).ConfigureAwait(false);
            }
            catch (SetupException ex)
            {
                // stderr, including under --json: there is no report to put it in, and an empty
                // stdout with a non-zero exit is the shape a consumer already handles.
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    ex switch
                    {
                        AmbiguousSolutionException => "  Cannot choose a solution: ",
                        Commands.ReportRefusedException => "  Cannot run this report: ",
                        _ => "  Nothing to analyse: ",
                    } + ex.Message);
                Console.Error.WriteLine();
                return ExitCodes.Inconclusive;
            }
        };
}
