using System.CommandLine;
using Tia.Core.Configuration;
using Tia.Workspace;

namespace Tia.DeadCode.Cli.Commands;

/// <summary>Reports project references nothing in the referencing project needs.</summary>
public static class ReferencesCommand
{
    public static Command Create()
    {
        var path = new Option<string>("--path", "-p")
        {
            Description = "Directory of the repository to scan.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };

        var solution = new Option<string?>("--solution", "-s")
        {
            Description = "Solution or project to scan. Discovered in --path when omitted.",
        };

        var command = new Command("references", "Report ProjectReferences whose assembly the project source never uses.");
        command.Options.Add(path);
        command.Options.Add(solution);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(path)!);
            var solutionPath = parseResult.GetValue(solution);

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
                return ExitCodes.Inconclusive;
            }

            // A project that did not load is not in the list, and a reference only it used would be
            // reported as unused. Same refusal as the scan.
            if (solutionGraph.Problems.Count > 0)
            {
                Console.Error.WriteLine("Nothing can be concluded: the solution did not load in full.");
                foreach (var problem in solutionGraph.Problems)
                {
                    Console.Error.WriteLine($"  x {problem}");
                }

                return ExitCodes.Inconclusive;
            }

            // A multi-targeted project is one loaded project per framework, each named with its
            // framework. The check groups them by project file, so a reference one framework uses
            // is kept for all.
            var fileOf = solutionGraph.Projects.ToDictionary(
                project => project.Descriptor.Name, project => project.Descriptor.FilePath, StringComparer.Ordinal);
            var found = UnusedProjectReferences.Find(
                [.. solutionGraph.Projects.Select(project => new ReferenceProject(
                    project.Descriptor.FilePath,
                    project.Compilation,
                    [.. project.Descriptor.ProjectReferences
                        .Where(fileOf.ContainsKey)
                        .Select(name => fileOf[name])
                        .Distinct(StringComparer.Ordinal)
                        .Where(file => UnusedProjectReferences.Lists(File.ReadAllText(project.Descriptor.FilePath), file))]))],
                cancellationToken);

            var reported = 0;
            var kept = 0;
            foreach (var unused in found)
            {
                if (UnusedProjectReferences.NamedElsewhere(File.ReadAllText(unused.Project), unused.Reference))
                {
                    kept++;
                    continue;
                }

                reported++;
                Console.Out.WriteLine($"{Path.GetRelativePath(root, unused.Project)} -> {Path.GetRelativePath(root, unused.Reference)}");
            }

            if (kept > 0)
            {
                Console.Error.WriteLine($"{kept} more are named elsewhere in the project file and were left out.");
            }

            return reported == 0 ? ExitCodes.Clean : ExitCodes.Findings;
        });

        return command;
    }
}
