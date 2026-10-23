using Tia.Core.Diff;

namespace Tia.Workspace;

/// <summary>
/// The command was pointed at something it cannot analyse at all: a <c>--path</c> that does not
/// exist or is not in a git repository, no solution or project to load, or several and nothing to
/// choose between them.
/// </summary>
/// <remarks>
/// <para>
/// Not an <see cref="InvalidOperationException"/>, for the reason
/// <see cref="AmbiguousSolutionException"/> gives: <see cref="SolutionAnalyzer"/> falls back to a
/// full run on any other failure, and a full run needs a solution to be a full run of. Without
/// one the fallback report named no project, so <c>analyze</c> and <c>graph</c> printed it and
/// exited 0, and <c>run</c> refused it with exit 1 as though a full run had been computed and
/// found empty (#50).
/// </para>
/// <para>
/// Every command turns it into exit 2: nothing was analysed, so nothing could be concluded.
/// </para>
/// </remarks>
public class SetupException(string message) : Exception(message)
{
    /// <summary>
    /// The solution to analyse: <paramref name="solutionPath"/> when one was given, otherwise the
    /// one discovered in <paramref name="directory"/>.
    /// </summary>
    /// <exception cref="SetupException">
    /// The directory does not exist, or nothing was given and nothing was found in it.
    /// </exception>
    /// <exception cref="AmbiguousSolutionException">Discovery found several of one kind.</exception>
    public static string RequireSolution(string directory, string? solutionPath)
    {
        if (solutionPath is not null)
        {
            return solutionPath;
        }

        RequireDirectory(directory);

        return WorkspaceLoader.FindSolutionOrProject(directory)
               ?? throw new SetupException(
                   $"no solution or project found in '{directory}'. Pass --path to the directory that holds " +
                   "one, or --solution to name it.");
    }

    /// <exception cref="SetupException">The directory does not exist.</exception>
    public static void RequireDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new SetupException($"'{directory}' does not exist. Pass --path to the directory that holds the solution.");
        }
    }

    /// <exception cref="SetupException">
    /// The directory does not exist, or is not inside a git repository.
    /// </exception>
    public static GitClient RequireRepository(string directory)
    {
        RequireDirectory(directory);

        return GitClient.Discover(directory)
               ?? throw new SetupException($"'{directory}' is not inside a git repository, so there is no diff to select tests from.");
    }
}
