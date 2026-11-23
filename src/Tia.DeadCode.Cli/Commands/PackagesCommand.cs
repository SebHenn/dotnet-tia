using System.CommandLine;

namespace Tia.DeadCode.Cli.Commands;

/// <summary>Reports package references nothing in the referencing project source needs.</summary>
public static class PackagesCommand
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

        var command = new Command("packages", "Report PackageReferences whose assemblies the project source never uses.");
        command.Options.Add(path);
        command.Options.Add(solution);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(path)!);
            if (await FullLoad.LoadAsync(root, parseResult.GetValue(solution), cancellationToken).ConfigureAwait(false) is not { } solutionGraph)
            {
                return ExitCodes.Inconclusive;
            }

            // One loaded project per framework; the check judges them together, so a package one
            // framework uses is kept for all.
            var projects = new List<PackageProject>();
            var skipped = 0;
            foreach (var group in solutionGraph.Projects.GroupBy(project => project.Descriptor.FilePath, StringComparer.Ordinal))
            {
                var assetsFile = Path.Combine(Path.GetDirectoryName(group.Key)!, "obj", "project.assets.json");
                if (!File.Exists(assetsFile))
                {
                    // Not restored where it is expected: nothing is said about this project.
                    skipped++;
                    continue;
                }

                projects.Add(new PackageProject(
                    group.Key,
                    [.. group.Select(project => project.Compilation)],
                    PackageAssets.Parse(File.ReadAllText(assetsFile))));
            }

            var found = UnusedPackages.Find(projects, cancellationToken);
            foreach (var unused in found)
            {
                Console.Out.WriteLine($"{Path.GetRelativePath(root, unused.Project)} -> {unused.Package}");
            }

            if (skipped > 0)
            {
                Console.Error.WriteLine($"{skipped} projects have no obj/project.assets.json and were not checked.");
            }

            return found.Count == 0 ? ExitCodes.Clean : ExitCodes.Findings;
        });

        return command;
    }
}
