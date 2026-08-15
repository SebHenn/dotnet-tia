using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using Tia.Frameworks;

namespace Tia.Workspace;

/// <param name="Evaluated">
/// True when MSBuild computed these, false when they were read out of project XML. Reported per
/// project in <c>--json</c>, because the two answer differently and the difference is not visible
/// from the values alone: XML sees what is written, evaluation sees what is true.
/// </param>
public sealed record ProbedProperties(IReadOnlyDictionary<string, string> Values, bool Evaluated);

/// <summary>
/// Reads the handful of MSBuild properties that decide which runner a test project uses.
/// </summary>
/// <remarks>
/// <para>
/// Evaluated through MSBuild, so a property set behind a condition, computed by a property
/// function, or supplied by the SDK itself is seen. Reading the XML instead - which is what this
/// did originally - sees only literals written in a project file or a <c>Directory.Build.props</c>,
/// and silently disagrees with the build about everything else.
/// </para>
/// <para>
/// The clearest example is <c>UsingMicrosoftNETSdkTest</c>: the runner detector has always tested
/// it, but nothing ever put it in the dictionary, because no project file writes it -
/// Microsoft.NET.Test.Sdk's props do. That branch could not fire, and the XML read is why. It fires
/// now for any restored project that references the package.
/// </para>
/// <para>
/// The XML read is kept as the fallback for a project evaluation cannot open - one needing an
/// uninstalled workload, or an SDK that will not resolve. Such a project must not become worse
/// than it was before evaluation existed, which is what a bare rethrow here would have made it.
/// </para>
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

        // Not used to detect anything - used to count. A multi-targeted project becomes one Roslyn
        // project per framework, so comparing how many arrived against how many were declared is
        // what catches a project that loaded for one framework and silently not for another.
        "TargetFramework",
        "TargetFrameworks",

        // Set by Microsoft.NET.Test.Sdk's own props, so it appears in no project file and the XML
        // read could never see it. FrameworkDetector has tested it since before this probe
        // evaluated anything, against a dictionary that could not contain it. Visible now, though
        // only for a restored project: an unrestored one imports no package props.
        "UsingMicrosoftNETSdkTest",
    ];

    /// <summary>
    /// The literal read: what the project XML says, with no conditions honoured and no expressions
    /// expanded. The fallback, and what every caller got before evaluation existed.
    /// </summary>
    /// <remarks>
    /// A separate overload rather than an optional <see cref="ProjectCollection"/> parameter, so
    /// that callers who do not evaluate need no reference to Microsoft.Build. An optional parameter
    /// puts the type in the signature and therefore in every caller's compile.
    /// </remarks>
    public static ProbedProperties Read(string projectFilePath, string repositoryRoot) =>
        Finish(ReadFromXml(projectFilePath, repositoryRoot), evaluated: false, repositoryRoot);

    /// <summary>Evaluates through a caller-owned collection, falling back to the literal read.</summary>
    public static ProbedProperties Read(string projectFilePath, string repositoryRoot, ProjectCollection collection)
    {
        var (properties, evaluated) = Evaluate(projectFilePath, repositoryRoot, collection);
        return Finish(properties, evaluated, repositoryRoot);
    }

    /// <summary>
    /// global.json's <c>test.runner</c> is repository-wide and not an MSBuild property at all, so
    /// it is folded in whichever way the properties themselves were obtained.
    /// </summary>
    private static ProbedProperties Finish(Dictionary<string, string> properties, bool evaluated, string repositoryRoot)
    {
        var globalJsonRunner = GlobalJson.ReadTestRunner(repositoryRoot);
        if (globalJsonRunner is not null)
        {
            properties["GlobalJsonTestRunner"] = globalJsonRunner;
        }

        return new ProbedProperties(properties, evaluated);
    }

    /// <summary>
    /// Evaluates one project through a collection of its own. For callers with a single project to
    /// ask about; a load that will ask about many should share one collection instead, so a project
    /// referenced by several others is evaluated once.
    /// </summary>
    public static ProbedProperties ReadEvaluated(string projectFilePath, string repositoryRoot)
    {
        using var collection = new ProjectCollection();
        return Read(projectFilePath, repositoryRoot, collection);
    }

    private static (Dictionary<string, string> Properties, bool Evaluated) Evaluate(
        string projectFilePath,
        string repositoryRoot,
        ProjectCollection collection)
    {
        try
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // LoadProject caches on the collection, so a project already evaluated for another
            // reason is not evaluated twice. The collection is owned by the caller and disposed
            // with the workspace.
            var project = collection.LoadProject(projectFilePath);

            foreach (var name in InterestingProperties)
            {
                var value = project.GetPropertyValue(name);
                if (value.Length > 0)
                {
                    properties[name] = value;
                }
            }

            return (properties, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad. MSBuild throws InvalidProjectFileException for most of this, but
            // an unresolvable SDK surfaces as whatever the resolver felt like throwing, and a
            // project that used to be probed by XML must not stop being probed at all.
            return (ReadFromXml(projectFilePath, repositoryRoot), false);
        }
    }

    private static Dictionary<string, string> ReadFromXml(string projectFilePath, string repositoryRoot)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in PropertyFilesFor(projectFilePath, repositoryRoot))
        {
            ReadInto(file, properties);
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
}
