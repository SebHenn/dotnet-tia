using Tia.Core.Model;

namespace Tia.Workspace;

/// <summary>
/// Whether the loaded workspace resolved enough to be worth reporting on at all.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="WorkspaceLoader.ReadFailures"/>, which asks whether a project is
/// missing. This asks the question that comes after: the projects are all present, they compiled
/// well enough to produce a graph, and there is still not a single test in a project that says it
/// is a test project. That is not a fact about a repository, it is a fact about a failed load - and
/// it used to be reported as a result, with a zero a reader has no baseline for and an exit code of
/// zero underneath it.
/// </para>
/// <para>
/// The case that found it: <c>MSBuildWorkspace</c> needs <c>obj/project.assets.json</c> to resolve
/// a <c>PackageReference</c>, and a CI runner that has not restored yet has no <c>obj/</c> at all.
/// The solution loads, project references resolve, most of the graph is built - and xunit's
/// <c>[Fact]</c> is an error type, so no method is a test, so the graph is empty of the one thing
/// it exists to select. Every command downstream then answers confidently about nothing: `analyze`
/// selects a full run of no tests, and `shadow` reports "nothing at risk" while watching an empty
/// set. Only `verify` caught it, and only because it refuses to pass without a usable sample.
/// </para>
/// <para>
/// So a suite that vanished is an error rather than a number, and the reason is named rather than
/// guessed at: a missing assets file is trivially checkable, and where one is missing the message
/// is simply that the restore has not happened.
/// </para>
/// </remarks>
public static class WorkspaceHealth
{
    /// <summary>The assets file as a reader would type it, relative to the project.</summary>
    private static readonly string AssetsFile = Path.Combine("obj", "project.assets.json");

    /// <summary>
    /// Where the restore writes the file that makes a package reference resolvable.
    /// </summary>
    /// <remarks>
    /// The default location, not the evaluated <c>ProjectAssetsFile</c>. That is deliberate and it
    /// is why nothing concludes anything from the absence alone: this is asked only about a project
    /// that has <i>already</i> failed, to say which of two failures it is, so a repository that
    /// relocates <c>obj/</c> gets the less specific of two true sentences rather than a false alarm.
    /// </remarks>
    public static string AssetsFilePath(string projectFilePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectFilePath)) ?? string.Empty, AssetsFile);

    /// <summary>
    /// Reasons the loaded workspace cannot be reported on, or empty when it can.
    /// </summary>
    /// <param name="assetsFilePresent">
    /// Whether a project has been restored, given its project file path. Injected so the rule can
    /// be tested without a restored solution on disk; the default reads the filesystem.
    /// </param>
    public static IReadOnlyList<string> Diagnose(
        IReadOnlyList<ProjectDescriptor> descriptors,
        IReadOnlyList<TestMethod> tests,
        Func<string, bool>? assetsFilePresent = null)
    {
        assetsFilePresent ??= path => File.Exists(AssetsFilePath(path));

        var counts = tests.GroupBy(t => t.ProjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var failures = new List<string>();

        // Grouped by file rather than by name, so a multi-targeted test project is one suite and
        // not one per framework. A framework that yields no tests while another yields hundreds is
        // ordinary conditional compilation; the whole project yielding none is the failure.
        foreach (var group in descriptors
                     .Where(d => d.IsTestProject)
                     .GroupBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Sum(d => counts.GetValueOrDefault(d.Name)) > 0)
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(group.Key);
            var restored = group.Key.Length > 0 && assetsFilePresent(group.Key);

            // Ordered by how actionable each answer is. An unrestored project explains itself and
            // the fix is one command; a restored project that still references no test framework
            // has a resolution problem of some other shape and should not be told to restore again.
            failures.Add((restored, group.All(d => d.Framework == TestFramework.Unknown)) switch
            {
                (false, _) =>
                    $"{name} is a test project and yielded 0 tests: it has not been restored - " +
                    $"{AssetsFile} is missing - so its package references did not resolve and no test " +
                    "framework attribute is a known type. Run `dotnet restore` first.",

                (true, true) =>
                    $"{name} is a test project and yielded 0 tests, and references no test framework " +
                    $"assembly at all. Its references did not resolve; {AssetsFile} is present, so a " +
                    "missing restore is not the explanation.",

                _ =>
                    $"{name} is a test project and yielded 0 tests, so nothing in it can ever be selected. " +
                    "If it deliberately contains none, set <IsTestProject>false</IsTestProject> so it is not " +
                    "counted as a suite that went missing.",
            });
        }

        failures.Sort(StringComparer.Ordinal);
        return failures;
    }
}
