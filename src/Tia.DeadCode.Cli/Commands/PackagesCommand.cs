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

        var all = new Option<bool>("--all")
        {
            Description = "Also list packages of a kind commonly loaded at run time (database providers, logging sinks, test hosts), marked low confidence.",
        };

        var command = new Command("packages", "Report PackageReferences whose assemblies the project source never uses.");
        command.Options.Add(path);
        command.Options.Add(solution);
        command.Options.Add(all);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(path)!);
            if (await FullLoad.LoadAsync(root, parseResult.GetValue(solution), cancellationToken).ConfigureAwait(false) is not { } solutionGraph)
            {
                return ExitCodes.Inconclusive;
            }

            // One loaded project per framework; the check judges them together, so a package one
            // framework uses is kept for all.
            var restored = RestoredAssets(root);
            var projects = new List<PackageProject>();
            var skipped = 0;
            foreach (var group in solutionGraph.Projects.GroupBy(project => project.Descriptor.FilePath, StringComparer.Ordinal))
            {
                if (!restored.TryGetValue(PackageAssets.Normalise(group.Key), out var assetsFile))
                {
                    // Not restored: nothing is said about this project.
                    skipped++;
                    continue;
                }

                projects.Add(new PackageProject(
                    group.Key,
                    [.. group.Select(project => project.Compilation)],
                    PackageAssets.Parse(File.ReadAllText(assetsFile))));
            }

            var found = UnusedPackages.Find(projects, cancellationToken);
            var showLow = parseResult.GetValue(all);
            var high = 0;
            var low = 0;
            var shared = 0;
            foreach (var unused in found)
            {
                if (!UnusedPackages.Declares(File.ReadAllText(unused.Project), unused.Package))
                {
                    shared++;
                    continue;
                }

                var confidence = PackageConfidence.Of(unused.Package);
                if (confidence == Confidence.High)
                {
                    high++;
                    Console.Out.WriteLine($"{Path.GetRelativePath(root, unused.Project)} -> {unused.Package}");
                }
                else
                {
                    low++;
                    if (showLow)
                    {
                        Console.Out.WriteLine($"{Path.GetRelativePath(root, unused.Project)} -> {unused.Package} (low confidence: commonly loaded at run time)");
                    }
                }
            }

            if (shared > 0)
            {
                Console.Error.WriteLine($"{shared} more come from a props or targets file shared with other projects and were left out.");
            }

            if (low > 0 && !showLow)
            {
                Console.Error.WriteLine($"{low} more are of a kind commonly loaded at run time; --all lists them.");
            }

            if (skipped > 0)
            {
                Console.Error.WriteLine($"{skipped} projects have no project.assets.json and were not checked.");
            }

            return high == 0 && (!showLow || low == 0) ? ExitCodes.Clean : ExitCodes.Findings;
        });

        return command;
    }

    /// <summary>Every restore result under the root, by the project it was restored for.</summary>
    private static Dictionary<string, string> RestoredAssets(string root)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "project.assets.json", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }))
        {
            if (PackageAssets.RestoredProject(File.ReadAllText(file)) is { } project)
            {
                // Two restores for one project (a stale copy): neither is trusted over the other.
                if (!found.TryAdd(project, file))
                {
                    found[project] = string.Empty;
                }
            }
        }

        return found.Where(pair => pair.Value.Length > 0).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}
