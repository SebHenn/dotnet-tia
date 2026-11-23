using Tia.Workspace;

namespace Tia.DeadCode.Cli.Commands;

/// <summary>Loads a solution for a command that may only answer from all of it.</summary>
internal static class FullLoad
{
    /// <summary>
    /// The loaded solution, or null after printing why nothing can be concluded. A project that
    /// did not load contributes no uses, so what only it needed would be reported as unused.
    /// </summary>
    public static async Task<SolutionGraph?> LoadAsync(string root, string? solutionPath, CancellationToken cancellationToken)
    {
        SolutionGraph solutionGraph;
        try
        {
            solutionGraph = await SolutionGraph.LoadAsync(
                root,
                solutionPath is null ? null : Path.GetFullPath(solutionPath, root),
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SetupException or AmbiguousSolutionException)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
        }

        if (solutionGraph.Problems.Count == 0)
        {
            return solutionGraph;
        }

        Console.Error.WriteLine("Nothing can be concluded: the solution did not load in full.");
        foreach (var problem in solutionGraph.Problems)
        {
            Console.Error.WriteLine($"  x {problem}");
        }

        return null;
    }
}
