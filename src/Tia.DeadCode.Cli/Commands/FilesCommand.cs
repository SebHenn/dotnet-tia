using System.CommandLine;

namespace Tia.DeadCode.Cli.Commands;

/// <summary>Reports source files in a loaded project's folder that no project compiles.</summary>
public static class FilesCommand
{
    private static readonly string[] BuildExtensions = [".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".projitems"];

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

        var command = new Command("files", "Report .cs files no project compiles and embedded resources no source names.");
        command.Options.Add(path);
        command.Options.Add(solution);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(path)!);
            if (await FullLoad.LoadAsync(root, parseResult.GetValue(solution), cancellationToken).ConfigureAwait(false) is not { } solutionGraph)
            {
                return ExitCodes.Inconclusive;
            }

            var sources = new List<string>();
            var buildFiles = new List<BuildFile>();
            foreach (var file in Enumerate(root))
            {
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    sources.Add(PackageAssets.Normalise(file));
                }
                else if (BuildExtensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                {
                    buildFiles.Add(new BuildFile(PackageAssets.Normalise(file), File.ReadAllText(file)));
                }
            }

            var compiled = solutionGraph.Projects
                .SelectMany(project => project.Compilation.SyntaxTrees)
                .Where(tree => tree.FilePath.Length > 0)
                .Select(tree => PackageAssets.Normalise(tree.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var loaded = solutionGraph.Projects
                .Select(project => PackageAssets.Normalise(project.Descriptor.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var found = OrphanedFiles.Find(new OrphanInput(sources, compiled, loaded, buildFiles));
            foreach (var orphan in found)
            {
                Console.Out.WriteLine(Path.GetRelativePath(root, orphan));
            }

            var resources = UnusedResources.Find(
                [.. solutionGraph.Projects
                    .GroupBy(project => project.Descriptor.FilePath, StringComparer.Ordinal)
                    .Select(group => new ResourceProject(group.Key, File.ReadAllText(group.Key), [.. group.Select(project => project.Compilation)]))],
                cancellationToken);
            foreach (var resource in resources)
            {
                Console.Out.WriteLine($"{Path.GetRelativePath(root, resource.Project)}: embedded resource {resource.Include} is never named");
            }

            return found.Count == 0 && resources.Count == 0 ? ExitCodes.Clean : ExitCodes.Findings;
        });

        return command;
    }

    /// <summary>Every file under the root except in bin, obj and hidden folders, which hold output and tooling.</summary>
    private static IEnumerable<string> Enumerate(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules")
            {
                continue;
            }

            foreach (var file in Enumerate(child))
            {
                yield return file;
            }
        }
    }
}
