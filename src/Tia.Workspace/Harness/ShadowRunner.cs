using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Frameworks;

namespace Tia.Workspace.Harness;

public enum ShadowVerdict
{
    /// <summary>At least one test failed, and every one of them was selected.</summary>
    Clean,

    /// <summary>A test failed that the selection would have skipped.</summary>
    Miss,

    /// <summary>
    /// The suite was green, so no failure existed to be missed.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Clean"/>. "No failing test was skipped" is true of every green
    /// run and says nothing whatever about the selection - it is the vacuous pass that
    /// <c>MutationHarnessResult.Passed</c> already refuses by requiring a usable sample, and
    /// reporting it as safety would let a repository accumulate a hundred green runs and conclude
    /// something none of them supports. Shadow mode only learns from red runs; this verdict says so
    /// rather than taking credit.
    /// </remarks>
    NoFailures,

    /// <summary>Nothing could be concluded: a full run, or a suite whose results could not be read.</summary>
    Inconclusive,
}

public sealed record ShadowResult
{
    public required ShadowVerdict Verdict { get; init; }

    public required AnalysisReport Report { get; init; }

    /// <summary>Tests that failed but were not selected. Empty is the answer everyone wants.</summary>
    public IReadOnlyList<string> Misses { get; init; } = [];

    public int FailingTests { get; init; }

    /// <summary>Projects whose results could not be read, so nothing about them was checked.</summary>
    public IReadOnlyList<string> Unobserved { get; init; } = [];

    public string? InconclusiveReason { get; init; }

    /// <summary>What the selection would have run, as a fraction of the whole suite.</summary>
    public double SelectedFraction => Report.TotalTests == 0
        ? 0
        : (double)Report.SelectedTests / Report.TotalTests;
}

/// <summary>
/// Runs the selection and the whole suite together, and reports what selecting would have cost.
/// </summary>
/// <remarks>
/// <para>
/// The mutation gate proves the engine cannot miss a fault that <em>it</em> injected into C#. That
/// is a strong statement and a narrow one: a mutation only ever edits a method body, only in a
/// language the engine parses, and only in a repository whose suite is green to begin with. Real
/// diffs change data files, configuration and generated input; real suites have failures that
/// predate the diff; real applications dispatch through containers, message buses and HTTP routes
/// that no static edge records. <c>docs/benchmarks.md</c> documents one such gap on a real
/// application and could not gate it, because that repository's tests need Docker.
/// </para>
/// <para>
/// Shadow mode is how a repository answers the question for itself, without being asked to trust
/// any of the above. It selects, then runs everything anyway, then reports which failures the
/// selection would have skipped. Nothing is skipped while it runs - that is the point - so it costs
/// the analysis on top of a suite that was going to run in full regardless, and it can be left on
/// for weeks before anyone decides whether to act on it.
/// </para>
/// <para>
/// A project running unfiltered cannot contribute a miss: it runs every test it has, so no failure
/// in it was ever at risk of being skipped. That is the same reasoning the mutation harness uses,
/// and it is why the two share <see cref="SuiteRunner"/> rather than answering it twice.
/// </para>
/// <para>
/// <b>It assumes the base was green.</b> A failure this diff did not cause - one already broken on
/// the base revision, or a flake - is still a test that failed and was not selected, so it is
/// reported as a miss. That is not a defect in the comparison: on the evidence available to a single
/// run the two are genuinely indistinguishable, and the alternative is to swallow real misses
/// whenever a suite happens to be red. The mutation harness sidesteps this by refusing to start on a
/// dirty tree and mutating from a known-good state; shadow mode cannot, because the whole point is
/// to run against a diff it did not choose. So a red baseline makes a run's misses uninformative
/// rather than wrong, and the reading is to fix the baseline, not the tool.
/// </para>
/// </remarks>
public sealed class ShadowRunner(AnalysisOptions options, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>
    /// Every test project, carrying its selection where it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AnalysisReport.Projects"/> deliberately omits a project the selection did not
    /// reach - there is no filter to describe, and <c>run</c> has nothing to invoke there. Running
    /// only that list would mean shadow mode never executes the projects it selected nothing from,
    /// which are precisely the projects a miss hides in: if <c>tia</c> would have run none of a
    /// project's tests and one of them fails, that failure was skipped.
    /// </para>
    /// <para>
    /// So the run list comes from the workspace and the selections are merged onto it. A project
    /// with no selection is marked <see cref="ProjectSelection.Filtered"/> rather than unfiltered,
    /// because "unfiltered" means the project runs whole and therefore cannot hide a miss - the
    /// opposite of what an empty selection means.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<ProjectSelection> ProjectsToRun(
        IReadOnlyList<ProjectDescriptor> descriptors,
        IReadOnlyList<ProjectSelection> selections)
    {
        var byName = selections.ToDictionary(p => p.Name, StringComparer.Ordinal);

        return
        [
            .. descriptors
                .Where(d => d.IsTestProject)
                .Select(d => byName.TryGetValue(d.Name, out var selection)
                    ? selection
                    : new ProjectSelection
                    {
                        Name = d.Name,
                        ProjectPath = d.FilePath,
                        Framework = d.Framework.ToString(),
                        Runner = d.Runner.ToString(),
                        TotalTests = 0,
                        SelectedTests = 0,
                        Filtered = true,
                        Tests = [],
                    }),
        ];
    }

    /// <summary>
    /// The failures that the selection would have skipped.
    /// </summary>
    /// <remarks>
    /// A project running unfiltered cannot contribute one: it runs every test it has, so nothing in
    /// it was ever at risk. That exemption is load-bearing rather than an optimisation - a selection
    /// that covers most of a project deliberately drops its filter, and counting those failures as
    /// misses would report a miss for every test in a project the tool had already decided to run
    /// whole.
    /// </remarks>
    internal static IReadOnlyList<string> FindMisses(
        IReadOnlyList<(string Project, string Test)> failures,
        IReadOnlyList<ProjectSelection> projects)
    {
        var selected = new HashSet<string>(projects.SelectMany(p => p.Tests), StringComparer.Ordinal);
        var unfiltered = new HashSet<string>(
            projects.Where(p => !p.Filtered).Select(p => p.Name), StringComparer.Ordinal);

        var misses = new List<string>();

        foreach (var (project, test) in failures)
        {
            if (unfiltered.Contains(project))
            {
                continue;
            }

            // Same comparison the mutation gate uses, deliberately: the two harnesses answer the
            // same question about the same selection, and a shadow run that disagreed with the gate
            // about what "selected" means would be reporting on something else.
            if (!MutationHarness.IsSelected(test, selected))
            {
                misses.Add(test);
            }
        }

        return misses;
    }

    public async Task<ShadowResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        var report = outcome.Report;

        // A full run selected nothing away, so there is nothing for it to have missed. Saying so is
        // more useful than reporting a clean verdict that was never in question.
        if (report.IsFullRun)
        {
            return new ShadowResult
            {
                Verdict = ShadowVerdict.Inconclusive,
                Report = report,
                InconclusiveReason = report.FullRunReasons.Count > 0
                    ? "the analysis bailed out to a full run, so no test was at risk of being skipped: " +
                      string.Join("; ", report.FullRunReasons)
                    : "the analysis bailed out to a full run, so no test was at risk of being skipped",
            };
        }

        var toRun = ProjectsToRun(outcome.Projects, report.Projects);

        if (toRun.Count == 0)
        {
            return new ShadowResult
            {
                Verdict = ShadowVerdict.Inconclusive,
                Report = report,
                InconclusiveReason = "no test project was found, so there was nothing to run",
            };
        }

        _log($"running all {report.TotalTests} test(s) to check a selection of {report.SelectedTests}");

        // No budget here, and deliberately. Shadow mode runs a real diff's suite once - there is no
        // injected fault that could have stopped a loop terminating, and the run it is compared
        // against is the one the repository would have done anyway.
        var suite = new SuiteRunner(options.RepositoryRoot, _log)
            .RunAll(toRun, TestCommandBuilder.ModeOf(report), timeout: null, cancellationToken);

        if (suite.Unobserved.Count > 0)
        {
            // Never a clean verdict from a run that could not be read. The same rule the mutation
            // gate applies, for the same reason: silence and success look identical otherwise.
            return new ShadowResult
            {
                Verdict = ShadowVerdict.Inconclusive,
                Report = report,
                Unobserved = suite.Unobserved,
                FailingTests = suite.Failures.Count,
                InconclusiveReason =
                    $"no TRX results were produced for {string.Join(", ", suite.Unobserved)} - shadow mode needs " +
                    "Microsoft.NET.Test.Sdk (VSTest) or Microsoft.Testing.Extensions.TrxReport " +
                    "(Microsoft.Testing.Platform) to read outcomes",
            };
        }

        var misses = FindMisses(suite.Failures, toRun);

        return new ShadowResult
        {
            Verdict = (misses.Count, suite.Failures.Count) switch
            {
                ( > 0, _) => ShadowVerdict.Miss,
                (_, 0) => ShadowVerdict.NoFailures,
                _ => ShadowVerdict.Clean,
            },
            Report = report,
            Misses = misses,
            FailingTests = suite.Failures.Count,
        };
    }

}
