using System.Text.RegularExpressions;

namespace Tia.DeadCode;

/// <summary>A build file (project, props, targets) and its text. The path uses <c>/</c>.</summary>
public sealed record BuildFile(string Path, string Text);

/// <summary>What the check reads. Every path uses <c>/</c> and is absolute.</summary>
/// <param name="SourceFiles">Every <c>.cs</c> file on disk under the repository, minus bin, obj and hidden folders.</param>
/// <param name="Compiled">Every file any loaded compilation contains, in any framework copy.</param>
/// <param name="LoadedProjects">The project files that loaded.</param>
/// <param name="BuildFiles">Every project, props and targets file under the repository.</param>
public sealed record OrphanInput(
    IReadOnlyList<string> SourceFiles,
    IReadOnlySet<string> Compiled,
    IReadOnlySet<string> LoadedProjects,
    IReadOnlyList<BuildFile> BuildFiles);

/// <summary>
/// Finds <c>.cs</c> files that no loaded project compiles.
/// </summary>
/// <remarks>
/// A file is reported only when every one of these holds, so each only drops a report:
/// no loaded compilation contains it in any framework copy; no project above it failed to load
/// (it might belong to a build this load did not see); no build file shapes compile items in a way
/// this check cannot read (an exclusion or condition is deliberate, and a glob or parent-folder
/// link anywhere could pull in a file that sits under no project); and no build file anywhere
/// names the file. In practice the finding is a file in a folder with no project above it, since
/// a loaded SDK project compiles every file in its folder by default.
/// </remarks>
public static partial class OrphanedFiles
{
    public static IReadOnlyList<string> Find(OrphanInput input)
    {
        var projectFiles = input.BuildFiles.Where(file => IsProject(file.Path)).ToList();
        var compiled = new HashSet<string>(input.Compiled, StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        foreach (var file in input.SourceFiles.Order(StringComparer.Ordinal))
        {
            if (compiled.Contains(file))
            {
                continue;
            }

            var projects = NearestProjects(file, projectFiles);
            if (projects.Any(project => !input.LoadedProjects.Contains(project.Path)))
            {
                continue;
            }

            // Under a loaded project the default globs compile the file unless something excludes
            // it, so only an exclusion this check cannot read could make it an orphan: stop there.
            // Under no project, any build file in the repository could pull it in by a glob or a
            // link, so one that does anything but list its own files stops the report.
            if (projects.Count > 0
                ? input.BuildFiles.Any(build => Governs(build.Path, file) && Evaluates(build.Text, strict: false))
                : input.BuildFiles.Any(build => Evaluates(build.Text, strict: true)))
            {
                continue;
            }

            var name = file[(file.LastIndexOf('/') + 1)..];
            if (input.BuildFiles.Any(build => build.Text.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            found.Add(file);
        }

        return found;
    }

    private static bool IsProject(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);

    private static string DirectoryOf(string path) => path[..Math.Max(path.LastIndexOf('/'), 0)];

    /// <summary>The project files in the closest folder above the file that has any.</summary>
    private static List<BuildFile> NearestProjects(string file, List<BuildFile> projects)
    {
        var directory = DirectoryOf(file);
        while (directory.Length > 0)
        {
            var here = projects.Where(project => string.Equals(DirectoryOf(project.Path), directory, StringComparison.OrdinalIgnoreCase)).ToList();
            if (here.Count > 0)
            {
                return here;
            }

            directory = DirectoryOf(directory);
        }

        return [];
    }

    /// <summary>A build file governs a source file when it sits in a folder above it, or is the project in its own folder.</summary>
    private static bool Governs(string buildFile, string file) =>
        file.StartsWith(DirectoryOf(buildFile) + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the build file's source items are shaped by something this load may not have
    /// evaluated. Strict also counts a glob or a parent-folder link of any item type, which can
    /// pull in a file from outside the project's own folder.
    /// </summary>
    private static bool Evaluates(string text, bool strict)
    {
        if (text.Contains("EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (Match item in (strict ? SourceItem() : CompileItem()).Matches(text))
        {
            var element = item.Value;
            if (element.Contains("Remove=", StringComparison.OrdinalIgnoreCase)
                || element.Contains("Update=", StringComparison.OrdinalIgnoreCase)
                || element.Contains("Condition=", StringComparison.OrdinalIgnoreCase)
                || element.Contains('*', StringComparison.Ordinal)
                || (strict && (element.Contains("..", StringComparison.Ordinal) || element.Contains("Link", StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"<Compile\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex CompileItem();

    [GeneratedRegex(@"<(Compile|None|Content|EmbeddedResource|AdditionalFiles|Page)\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex SourceItem();
}
