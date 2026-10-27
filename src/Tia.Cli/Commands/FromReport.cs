using System.Text.Json;
using Tia.Cli.Server;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>
/// A report that one job writes and other jobs run: analyse once, then run in every shard and on
/// every OS of a matrix (#129).
/// </summary>
/// <remarks>
/// <para>
/// Every CI leg used to run its own analysis, cold, because the action keeps no cache: 8 to 17
/// seconds on the measured repositories before a test ran, multiplied by the shard count and the
/// OS matrix. The report already held everything a run needs, but could not safely be run
/// elsewhere: it named its projects by absolute path, which is another path in another job and
/// another shape on another OS, and nothing said which version had written it.
/// </para>
/// <para>
/// Running one is all or nothing. A report from another commit or another version is refused, never
/// run on a guess: its selection is a statement about one commit made by one engine.
/// </para>
/// </remarks>
public static class FromReport
{
    /// <summary>
    /// What a written report carries so another checkout can run it: the version that wrote it, each
    /// project's path relative to the repository, and, when asked for, the test inventory a shard
    /// is divided by.
    /// </summary>
    /// <param name="inventory">Every discovered test, for <c>--report-inventory</c>; null keeps what the report had.</param>
    public static AnalysisReport Stamp(AnalysisReport report, string repositoryRoot, IReadOnlyList<TestMethod>? inventory)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report with
        {
            ToolVersion = ServerProtocol.ToolVersion,
            Projects = [.. report.Projects.Select(p => p with { RelativeProjectPath = Relative(repositoryRoot, p.ProjectPath) })],
            UnanalysedTestProjects =
            [
                .. report.UnanalysedTestProjects.Select(p => p with { RelativeProjectPath = Relative(repositoryRoot, p.ProjectPath) }),
            ],
            TestInventory = inventory is null ? report.TestInventory : Inventory(report, inventory),
        };
    }

    /// <summary>
    /// Reads a report to run here, with its projects found in this checkout, and the tests its
    /// inventory names (empty without one).
    /// </summary>
    /// <exception cref="ReportRefusedException">Unreadable, another schema, another version, or another commit.</exception>
    public static (AnalysisReport Report, IReadOnlyList<TestMethod> AllTests) Load(string path, string repositoryRoot, IGitClient git)
    {
        ArgumentNullException.ThrowIfNull(git);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReportRefusedException($"{path} could not be read: {ex.Message}");
        }

        AnalysisReport? report;
        try
        {
            using (var document = JsonDocument.Parse(json))
            {
                if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                    schema.ValueKind != JsonValueKind.Number ||
                    schema.GetInt32() != AnalysisReport.SchemaVersion)
                {
                    throw new ReportRefusedException(
                        $"{path} is not a dotnet-tia report of schema version {AnalysisReport.SchemaVersion}.");
                }
            }

            report = AnalysisReport.FromJson(json);
        }
        catch (JsonException ex)
        {
            throw new ReportRefusedException($"{path} is not a dotnet-tia report: {ex.Message}");
        }

        if (report is null)
        {
            throw new ReportRefusedException($"{path} is not a dotnet-tia report.");
        }

        if (report.ToolVersion is null)
        {
            throw new ReportRefusedException(
                $"{path} does not say which dotnet-tia wrote it, so it was written before reports could be run. Analyse again.");
        }

        if (!string.Equals(report.ToolVersion, ServerProtocol.ToolVersion, StringComparison.Ordinal))
        {
            throw new ReportRefusedException(
                $"{path} was written by dotnet-tia {report.ToolVersion}, and this is {ServerProtocol.ToolVersion}. " +
                "Run it with the version that wrote it, or analyse again.");
        }

        var head = git.HeadCommit();
        if (report.HeadCommit is null || head is null || !string.Equals(report.HeadCommit, head, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReportRefusedException(
                $"{path} was analysed at {report.HeadCommit ?? "an unrecorded commit"}, and HEAD here is {head ?? "unknown"}. " +
                "A selection holds for the commit it was made at only.");
        }

        var rebased = report with
        {
            Projects = [.. report.Projects.Select(p => p.RelativeProjectPath is { } relative ? p with { ProjectPath = Absolute(repositoryRoot, relative) } : p)],
            UnanalysedTestProjects =
            [
                .. report.UnanalysedTestProjects.Select(p => p.RelativeProjectPath is { } relative ? p with { ProjectPath = Absolute(repositoryRoot, relative) } : p),
            ],
        };

        return (rebased, Tests(report.TestInventory));
    }

    /// <summary>The inventory's tests, as discovery described them.</summary>
    public static IReadOnlyList<TestMethod> Tests(IReadOnlyList<InventoryProject>? inventory) =>
    [
        .. (inventory ?? []).SelectMany(project => project.Tests.Select(test =>
        {
            var className = test.Namespace.Length == 0 ? test.ClassName : $"{test.Namespace}.{test.ClassName}";
            return new TestMethod
            {
                // Graph keys are not in a report, and nothing that runs a selection reads them.
                SymbolKey = $"{className}.{test.MethodName}",
                ClassKey = className,
                Namespace = test.Namespace,
                ClassName = test.ClassName,
                MethodName = test.MethodName,
                ProjectName = project.Project,
                Framework = Enum.TryParse<TestFramework>(project.Framework, out var framework) ? framework : TestFramework.Unknown,
                IsParameterized = test.Parameterized,
            };
        })),
    ];

    private static IReadOnlyList<InventoryProject> Inventory(AnalysisReport report, IReadOnlyList<TestMethod> tests)
    {
        var byProject = tests.GroupBy(t => t.ProjectName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return
        [
            .. report.Projects
                .Select(p => p.Name)
                .Distinct(StringComparer.Ordinal)
                .Where(byProject.ContainsKey)
                .Select(name => new InventoryProject
                {
                    Project = name,
                    Framework = byProject[name][0].Framework.ToString(),
                    Tests =
                    [
                        .. byProject[name]
                            .OrderBy(t => t.FullyQualifiedName, StringComparer.Ordinal)
                            .Select(t => new InventoryTest
                            {
                                Namespace = t.Namespace,
                                ClassName = t.ClassName,
                                MethodName = t.MethodName,
                                Parameterized = t.IsParameterized,
                            }),
                    ],
                }),
        ];
    }

    /// <summary>Relative to the repository with forward slashes, or null for a path outside it.</summary>
    private static string? Relative(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path);
        return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? null
            : relative.Replace('\\', '/');
    }

    private static string Absolute(string repositoryRoot, string relative) =>
        Path.GetFullPath(Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
}

/// <summary>A report <c>run --from-report</c> will not run, and why.</summary>
public sealed class ReportRefusedException(string message) : SetupException(message);
