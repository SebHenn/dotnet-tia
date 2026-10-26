using System.Xml.Linq;
using Tia.Core.Model;
using Tia.Core.Reporting;

namespace Tia.Frameworks;

/// <summary>
/// Writes a filter too long for the command line to a file, which the runner reads instead (#90).
/// </summary>
/// <remarks>
/// <para>
/// The file has to be read by whatever finally runs the tests. <c>dotnet test @args.rsp</c> looks
/// like the obvious answer and is not one: the CLI expands the file, and MSBuild then starts the
/// runner with the expanded command line, which fails on Windows at the same length as before.
/// Measured on the fixtures with a 48,000-character filter, both on VSTest and on the
/// Microsoft.Testing.Platform bridge. Two forms survive, one per runner:
/// </para>
/// <list type="bullet">
/// <item>Microsoft.Testing.Platform reads response files itself, so <c>-- @filter.rsp</c> reaches
/// the test application unexpanded. That holds on the VSTest bridge and on the platform's own
/// <c>dotnet test</c> alike.</item>
/// <item>VSTest takes its filter from RunSettings too (<c>RunConfiguration/TestCaseFilter</c>), so
/// <c>--settings filter.runsettings</c>. Only where the project and the caller have no run settings
/// of their own: a <c>--settings</c> replaces the project's <c>RunSettingsFilePath</c> rather than
/// adding to it, and merging the two would move the user's file, and every relative path in it,
/// into a temporary directory.</item>
/// </list>
/// <para>
/// A runner too old to know <c>TestCaseFilter</c> ignores it and runs the project whole, which is
/// what happened to every filter this long before.
/// </para>
/// </remarks>
public sealed class FilterFiles : IDisposable
{
    private readonly Lazy<string> directory = new(() => Directory.CreateTempSubdirectory("tia-filters-").FullName);
    private int written;

    /// <summary>Leave the files in place when disposed, so a dry run's command can be run by hand.</summary>
    public bool Keep { get; init; }

    /// <summary>Whether a project's runner can be handed its filter in a file at all.</summary>
    public static bool Allowed(TestRunner runner, bool hasRunSettings) => runner switch
    {
        TestRunner.MicrosoftTestingPlatform => true,
        TestRunner.VsTest => !hasRunSettings,
        _ => false,
    };

    /// <inheritdoc cref="Allowed(TestRunner, bool)"/>
    public static bool Allowed(ProjectSelection project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Allowed(TestCommandBuilder.RunnerOf(project), project.HasRunSettings);
    }

    /// <summary>
    /// Whether these arguments can be written in the form the runner reads. Every argument is
    /// quoted in a response file, so none may hold a quote or a line break; and VSTest's file holds
    /// one filter expression, so its arguments must be exactly <c>--filter &lt;expression&gt;</c>.
    /// </summary>
    public static bool CanCarry(TestRunner runner, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return runner switch
        {
            TestRunner.MicrosoftTestingPlatform => arguments.Count > 0 && arguments.All(a => a.IndexOfAny(['"', '\r', '\n']) < 0),
            TestRunner.VsTest => arguments is ["--filter", _],
            _ => false,
        };
    }

    /// <summary>
    /// Whether the caller's own arguments rule the file out for this project: a VSTest project run
    /// with <c>--settings</c> of the caller's, which the generated file would have to replace.
    /// </summary>
    public static bool ConflictsWith(ProjectSelection project, IReadOnlyList<string> passthrough)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(passthrough);

        if (!project.FilterInFile || TestCommandBuilder.RunnerOf(project) != TestRunner.VsTest)
        {
            return false;
        }

        var separator = passthrough.ToList().IndexOf("--");
        var forDotnetTest = separator < 0 ? passthrough : passthrough.Take(separator);
        return forDotnetTest.Any(a => a is "--settings" or "-s" ||
                                      a.StartsWith("--settings:", StringComparison.Ordinal) ||
                                      a.StartsWith("--settings=", StringComparison.Ordinal));
    }

    /// <summary>The arguments that point the runner at a file holding <paramref name="filterArguments"/>.</summary>
    public IReadOnlyList<string> Write(TestRunner runner, IReadOnlyList<string> filterArguments)
    {
        if (!CanCarry(runner, filterArguments))
        {
            throw new ArgumentException($"These filter arguments cannot be passed to {runner} in a file.", nameof(filterArguments));
        }

        var number = Interlocked.Increment(ref written);
        if (runner == TestRunner.MicrosoftTestingPlatform)
        {
            var path = Path.Combine(directory.Value, $"filter-{number}.rsp");
            File.WriteAllText(path, ResponseFile(filterArguments));
            return ["@" + path];
        }

        var settings = Path.Combine(directory.Value, $"filter-{number}.runsettings");
        File.WriteAllText(settings, RunSettings(filterArguments[1]));
        return ["--settings", settings];
    }

    /// <summary>One argument per line, each quoted, which is how the platform reads them back.</summary>
    internal static string ResponseFile(IReadOnlyList<string> arguments) =>
        string.Concat(arguments.Select(a => $"\"{a}\"\n"));

    internal static string RunSettings(string filter) =>
        new XDocument(
            new XElement("RunSettings",
                new XElement("RunConfiguration",
                    new XElement("TestCaseFilter", filter)))).ToString() + "\n";

    public void Dispose()
    {
        if (Keep || !directory.IsValueCreated)
        {
            return;
        }

        try
        {
            Directory.Delete(directory.Value, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary directory the system will clear; not worth failing a finished run over.
        }
    }
}
