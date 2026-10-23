using System.CommandLine;
using Tia.Workspace;

namespace Tia.Cli;

/// <summary>
/// How every command answers when discovery finds several solutions and will not pick one.
/// </summary>
/// <remarks>
/// <para>
/// One wrapper rather than a catch in each command, because the answer has to be the same
/// everywhere and the commands reach discovery by different roads: <c>analyze</c>, <c>graph</c>
/// and <c>explain</c> through the analyzer, <c>shadow</c> and <c>verify</c> through a harness that
/// runs one, <c>run</c> before it starts its build, <c>watch</c> directly. A catch written into
/// each would be seven chances for one to say 1, or to print a stack trace (issue #37).
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
            catch (AmbiguousSolutionException ex)
            {
                // stderr, including under --json: there is no report to put it in, and an empty
                // stdout with a non-zero exit is the shape a consumer already handles.
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Cannot choose a solution: " + ex.Message);
                Console.Error.WriteLine();
                return ExitCodes.Inconclusive;
            }
        };
}
