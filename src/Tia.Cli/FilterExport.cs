using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks;

namespace Tia.Cli;

/// <summary>
/// Each test project's selection as files that its own job hands to <c>dotnet test</c>, for a
/// pipeline that builds once and runs every test project in a job of its own (#159).
/// </summary>
/// <remarks>
/// <para>
/// <c>run --from-report</c> needs tia, the checkout and HEAD in every job. A pipeline that ships each
/// test project as an artifact has none of them where the tests run, so the decision travels as
/// files the runner reads itself: a RunSettings <c>TestCaseFilter</c> on VSTest, and on
/// Microsoft.Testing.Platform a response file the test application reads unexpanded. These are
/// the forms <see cref="FilterFiles"/> uses for a filter too long for the command line (#90).
/// </para>
/// <para>
/// The manifest names every test project the analysis knows, and says for each whether to run it
/// filtered, whole, or not at all. Each job looks itself up. A project that is missing from the
/// manifest has to run whole. If a missing project meant "skip", a misspelt name would skip
/// tests; this way it runs too many.
/// </para>
/// </remarks>
public static class FilterExport
{
    public const string ManifestName = "tia-filters.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The manifest and the files it names, by file name. A project that is not selected, or not
    /// filtered, has no file.
    /// </summary>
    /// <param name="report">As written, so that its projects carry their relative paths.</param>
    /// <param name="testProjects">The analysed test projects, for those the selection left out; empty from a server.</param>
    /// <param name="allTests">Every discovered test, for the left-out projects a server did not describe.</param>
    public static (FilterManifest Manifest, IReadOnlyDictionary<string, string> Files) Build(
        AnalysisReport report,
        IReadOnlyList<ProjectDescriptor> testProjects,
        IReadOnlyList<TestMethod> allTests,
        string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(testProjects);
        ArgumentNullException.ThrowIfNull(allTests);

        var entries = new List<FilterManifestProject>();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in report.Projects)
        {
            var key = Key(project.Name, project.TargetFramework);
            var entry = new FilterManifestProject
            {
                Project = project.Name,
                RelativeProjectPath = project.RelativeProjectPath,
                Assembly = project.AssemblyPath is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : null,
                TargetFramework = project.TargetFramework,
                Runner = project.Runner,
                Run = FilterManifestProject.All,
                SelectedTests = project.SelectedTests,
                TotalTests = project.TotalTests,
                Reason = report.IsFullRun ? "full run" : project.UnfilteredReason ?? "runs whole",
            };

            if (project.Filtered && project.FilterArguments.Count > 0)
            {
                entry = WithFiles(entry, project, key, files);
            }

            entries.Add(entry);
        }

        foreach (var project in report.UnanalysedTestProjects)
        {
            entries.Add(new FilterManifestProject
            {
                Project = project.Name,
                RelativeProjectPath = project.RelativeProjectPath,
                Assembly = project.Assembly,
                Runner = null,
                Run = project.Runs ? FilterManifestProject.All : FilterManifestProject.None,
                Reason = project.Reason,
            });
        }

        // Selective only: a full run lists every analysed test project in Projects.
        if (!report.IsFullRun)
        {
            var listed = new HashSet<string>(report.Projects.Select(p => p.Name), StringComparer.Ordinal);
            var counts = allTests.GroupBy(t => t.ProjectName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var described = testProjects
                .Where(d => d.IsTestProject && !listed.Contains(d.Name))
                .DistinctBy(d => (d.Name, d.TargetFramework))
                .Select(d => new FilterManifestProject
                {
                    Project = d.Name,
                    RelativeProjectPath = Commands.FromReport.Relative(repositoryRoot, d.FilePath),
                    Assembly = d.AssemblyName,
                    TargetFramework = d.TargetFramework,
                    Runner = d.Runner.ToString(),
                    Run = FilterManifestProject.None,
                    TotalTests = counts.GetValueOrDefault(d.Name),
                    Reason = "no test selected",
                })
                .ToList();

            // From a server, which reports tests but not projects, the names are all there is.
            var named = counts.Keys
                .Where(name => !listed.Contains(name) && described.All(d => d.Project != name))
                .Order(StringComparer.Ordinal)
                .Select(name => new FilterManifestProject
                {
                    Project = name,
                    Run = FilterManifestProject.None,
                    TotalTests = counts[name],
                    Reason = "no test selected",
                });

            entries.AddRange(described);
            entries.AddRange(named);
        }

        var manifest = new FilterManifest
        {
            Mode = report.Mode,
            HeadCommit = report.HeadCommit,
            ToolVersion = report.ToolVersion,
            Projects = entries,
        };

        return (manifest, files);
    }

    /// <summary>
    /// Writes the manifest and its files to <paramref name="directory"/>, first removing the files
    /// that an earlier manifest there named, so that nothing is left over from an older selection.
    /// </summary>
    public static void Write(string directory, FilterManifest manifest, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);

        Directory.CreateDirectory(directory);
        RemovePrevious(directory);

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(directory, name), content, encoding);
        }

        File.WriteAllText(Path.Combine(directory, ManifestName), Serialize(manifest), encoding);
    }

    public static string Serialize(FilterManifest manifest) => JsonSerializer.Serialize(manifest, Options) + "\n";

    private static FilterManifestProject WithFiles(
        FilterManifestProject entry,
        ProjectSelection project,
        string key,
        Dictionary<string, string> files)
    {
        var runner = TestCommandBuilder.RunnerOf(project);
        var arguments = project.FilterArguments;

        if (runner == TestRunner.MicrosoftTestingPlatform && FilterFiles.CanCarry(runner, arguments))
        {
            var name = key + ".rsp";
            files[name] = FilterFiles.ResponseFile(arguments);
            return entry with { Run = FilterManifestProject.Filtered, Reason = null, ResponseFile = name };
        }

        if (runner == TestRunner.VsTest && FilterFiles.CanCarry(runner, arguments))
        {
            // The bare expression always, for a job that merges it into run settings of its own;
            // a RunSettings file only where the project has none for it to replace.
            var filter = key + ".filter";
            files[filter] = arguments[1] + "\n";
            string? settings = null;
            if (!project.HasRunSettings)
            {
                settings = key + ".runsettings";
                files[settings] = FilterFiles.RunSettings(arguments[1]);
            }

            return entry with { Run = FilterManifestProject.Filtered, Reason = null, Filter = filter, RunSettings = settings };
        }

        // Arguments no file can carry run whole, as run would when it could not pass them.
        return entry with { Reason = "its filter cannot be written to a file" };
    }

    private static void RemovePrevious(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path))
        {
            return;
        }

        FilterManifest? previous;
        try
        {
            previous = JsonSerializer.Deserialize<FilterManifest>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return;
        }

        foreach (var project in previous?.Projects ?? [])
        {
            foreach (var name in new[] { project.ResponseFile, project.Filter, project.RunSettings })
            {
                // Only a bare file name, as this class writes them: never a path out of the directory.
                if (name is { Length: > 0 } && Path.GetFileName(name) == name)
                {
                    File.Delete(Path.Combine(directory, name));
                }
            }
        }
    }

    /// <summary>The file name stem: the project, and the framework when it was analysed per framework.</summary>
    private static string Key(string project, string? targetFramework) =>
        targetFramework is { Length: > 0 } ? $"{project}.{targetFramework}" : project;
}

/// <summary>The manifest <c>--write-filters</c> writes (#159).</summary>
public sealed record FilterManifest
{
    /// <summary>The same version as the report's: it goes up only when a field changes meaning.</summary>
    [JsonPropertyOrder(-1)]
    public int SchemaVersion => AnalysisReport.SchemaVersion;

    /// <summary><c>selective</c> or <c>full</c>.</summary>
    public required string Mode { get; init; }

    public string? HeadCommit { get; init; }

    public string? ToolVersion { get; init; }

    public IReadOnlyList<FilterManifestProject> Projects { get; init; } = [];
}

public sealed record FilterManifestProject
{
    public const string Filtered = "filtered";
    public const string All = "all";
    public const string None = "none";

    public required string Project { get; init; }

    public string? RelativeProjectPath { get; init; }

    /// <summary>The built assembly's name, without extension, which an artifact is usually named by.</summary>
    public string? Assembly { get; init; }

    /// <summary>Set when a multi-targeted project was analysed per framework.</summary>
    public string? TargetFramework { get; init; }

    public string? Runner { get; init; }

    /// <summary><c>filtered</c>, <c>all</c> or <c>none</c>.</summary>
    public required string Run { get; init; }

    public int SelectedTests { get; init; }

    public int TotalTests { get; init; }

    public string? Reason { get; init; }

    /// <summary>Microsoft.Testing.Platform: pass as <c>@file</c> to the test application.</summary>
    public string? ResponseFile { get; init; }

    /// <summary>VSTest: the filter expression alone.</summary>
    public string? Filter { get; init; }

    /// <summary>VSTest: pass as <c>--settings</c>. Null when the project has run settings of its own.</summary>
    public string? RunSettings { get; init; }
}
