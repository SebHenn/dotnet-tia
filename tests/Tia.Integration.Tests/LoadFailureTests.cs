using Tia.Core.Model;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// Which workspace diagnostics mean a project is missing, and which only mean MSBuild had
/// something to say about one that loaded anyway.
/// </summary>
/// <remarks>
/// <para>
/// Found on MediatR. Its test project multi-targets net462 and references two packages that warn
/// they do not support it - an ordinary warning, and <c>dotnet build</c> reports zero errors. But
/// <c>MSBuildWorkspace</c> raises those through the same <c>WorkspaceDiagnosticKind.Failure</c>
/// channel as a real load error, wrapped in the same "failed when processing the file" sentence, so
/// every analysis of that repository bailed out to a full run and every mutation sample was
/// unusable. Twenty samples, zero of them able to say anything.
/// </para>
/// <para>
/// The rule these tests pin: the diagnostic is evidence, the loaded solution is the verdict.
/// </para>
/// </remarks>
public sealed class LoadFailureTests
{
    private const string Path = "/repo/test/Suite/Suite.csproj";

    private static ProjectDescriptor Project(
        string filePath,
        bool isTestProject = false,
        params string[] declaredFrameworks) => new()
    {
        Name = System.IO.Path.GetFileNameWithoutExtension(filePath),
        AssemblyName = System.IO.Path.GetFileNameWithoutExtension(filePath),
        FilePath = filePath,
        ProjectReferences = [],
        IsTestProject = isTestProject,
        DeclaredTargetFrameworks = declaredFrameworks,
    };

    /// <summary>
    /// The MediatR case itself. A complaint about a project that is standing right there is not a
    /// reason to distrust the graph.
    /// </summary>
    [Fact]
    public void A_complaint_about_a_project_that_loaded_is_not_a_failure()
    {
        var diagnostic =
            $"Failure: MSBuild failed when processing the file '{Path}' with message: " +
            "Microsoft.Extensions.Telemetry.Abstractions 10.0.0 doesn't support net462.";

        var logged = new List<string>();
        var failures = WorkspaceLoader.ReadFailures([diagnostic], [Project(Path)], [], logged.Add);

        Assert.Empty(failures);

        // Logged rather than dropped: it is still the only record that MSBuild objected at all.
        var line = Assert.Single(logged);
        Assert.Contains(diagnostic, line, StringComparison.Ordinal);
        Assert.StartsWith("the project loaded anyway", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conservative default has to survive the fix. Nothing in the loaded solution accounts for
    /// this project, so its tests genuinely cannot be reasoned about.
    /// </summary>
    [Fact]
    public void A_complaint_about_a_project_that_is_absent_still_fails()
    {
        var diagnostic =
            "Failure: MSBuild failed when processing the file '/repo/src/Missing/Missing.csproj' " +
            "with message: NETSDK1045.";

        var failures = WorkspaceLoader.ReadFailures([diagnostic], [Project(Path)], []);

        Assert.Equal([diagnostic], failures);
    }

    /// <summary>
    /// A project the engine cannot analyse is still a project that loaded. Forgiving the diagnostic
    /// costs nothing here: Phase 2's foreign-project widening is what covers a change inside it, and
    /// that path does not run on load failures.
    /// </summary>
    [Fact]
    public void A_complaint_about_a_foreign_project_that_loaded_is_not_a_failure()
    {
        const string vb = "/repo/src/Legacy/Legacy.vbproj";
        var diagnostic = $"Failure: MSBuild failed when processing the file '{vb}' with message: whatever.";

        var failures = WorkspaceLoader.ReadFailures(
            [diagnostic],
            [],
            [new ForeignProject("Legacy", "Visual Basic", vb, "/repo/src/Legacy")]);

        Assert.Empty(failures);
    }

    /// <summary>
    /// The hole the forgiveness opens. A multi-targeted project arrives as one loaded project per
    /// framework, so it can arrive for one and not another - and the diagnostic saying so names a
    /// path that <i>did</i> load, which the rule above would forgive. Tests nothing can see are a
    /// miss, so the count is checked separately and does not depend on a diagnostic at all.
    /// </summary>
    [Fact]
    public void A_test_project_short_of_a_target_framework_is_a_failure()
    {
        var failures = WorkspaceLoader.ReadFailures(
            [],
            [Project(Path, isTestProject: true, "net10.0", "net462")],
            []);

        var failure = Assert.Single(failures);
        Assert.Contains("Suite.csproj", failure, StringComparison.Ordinal);
        Assert.Contains("net462", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_project_that_loaded_for_every_framework_is_not_a_failure()
    {
        var failures = WorkspaceLoader.ReadFailures(
            [],
            [
                Project(Path, isTestProject: true, "net10.0", "net462"),
                Project(Path, isTestProject: true, "net10.0", "net462"),
            ],
            []);

        Assert.Empty(failures);
    }

    /// <summary>
    /// Nothing is concluded from a count that was never taken. Properties are evaluated only for
    /// projects the referenced-assembly signal already marks as tests, so an unevaluated project
    /// declares an empty list here - which must read as "unknown", not as "zero frameworks".
    /// </summary>
    [Fact]
    public void An_unknown_framework_count_concludes_nothing()
    {
        Assert.Empty(WorkspaceLoader.ReadFailures([], [Project(Path, isTestProject: true)], []));
    }

    /// <summary>
    /// A non-test project short of a framework is not the same risk. Its symbols still arrive
    /// through the framework that did load - the source files are the same - so the graph has its
    /// edges, and no test is invisible because of it.
    /// </summary>
    [Fact]
    public void A_library_short_of_a_target_framework_is_left_alone()
    {
        Assert.Empty(WorkspaceLoader.ReadFailures(
            [],
            [Project("/repo/src/Lib/Lib.csproj", isTestProject: false, "net10.0", "net462")],
            []));
    }
}
