using System.Text.Json;
using System.Xml.Linq;

namespace Tia.Workspace;

/// <summary>
/// Reads the handful of MSBuild properties that decide which runner a test project uses.
/// </summary>
/// <remarks>
/// This is a deliberate shortcut: the properties in question (<c>TestingPlatformDotnetTestSupport</c>
/// and friends) are set as literals in a project file or a <c>Directory.Build.props</c>, so a
/// direct XML read gets them without a second full MSBuild evaluation. Conditions and property
/// functions are not honoured; when the value cannot be read the detector falls back to the
/// referenced assemblies, which is the more reliable signal anyway.
/// </remarks>
public static class MsBuildPropertyProbe
{
    private static readonly string[] InterestingProperties =
    [
        "UseMicrosoftTestingPlatformRunner",
        "TestingPlatformDotnetTestSupport",
        "EnableMSTestRunner",
        "EnableNUnitRunner",
        "IsTestProject",
        "OutputType",
    ];

    public static IReadOnlyDictionary<string, string> Read(string projectFilePath, string repositoryRoot)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in PropertyFilesFor(projectFilePath, repositoryRoot))
        {
            ReadInto(file, properties);
        }

        var globalJsonRunner = ReadGlobalJsonRunner(repositoryRoot);
        if (globalJsonRunner is not null)
        {
            properties["GlobalJsonTestRunner"] = globalJsonRunner;
        }

        return properties;
    }

    /// <summary>Directory.Build.props from the repository root down, then the project itself.</summary>
    private static IEnumerable<string> PropertyFilesFor(string projectFilePath, string repositoryRoot)
    {
        var directories = new List<string>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        var root = Path.GetFullPath(repositoryRoot);

        while (directory is not null)
        {
            directories.Insert(0, directory);
            if (string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        foreach (var candidate in directories)
        {
            var props = Path.Combine(candidate, "Directory.Build.props");
            if (File.Exists(props))
            {
                yield return props;
            }
        }

        yield return projectFilePath;
    }

    private static void ReadInto(string path, Dictionary<string, string> properties)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var group in document.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
        {
            foreach (var element in group.Elements())
            {
                var name = element.Name.LocalName;
                if (Array.IndexOf(InterestingProperties, name) < 0)
                {
                    continue;
                }

                properties[name] = element.Value.Trim();
            }
        }
    }

    private static string? ReadGlobalJsonRunner(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "global.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("test", out var test) &&
                test.TryGetProperty("runner", out var runner) &&
                runner.ValueKind == JsonValueKind.String)
            {
                return runner.GetString();
            }
        }
        catch (Exception)
        {
            // A malformed global.json is the build system's problem, not ours.
        }

        return null;
    }
}
