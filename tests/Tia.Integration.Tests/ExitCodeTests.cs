using Tia.Cli;
using Tia.Cli.Commands;
using Tia.Workspace.Harness;

namespace Tia.Integration.Tests;

/// <summary>
/// How <c>verify</c> turns a finished run into an exit code.
/// </summary>
/// <remarks>
/// It used to return 1 for every answer that was not a pass. On 2026-10-20 the gate on tia itself
/// had 0 usable samples - 14 of them false out-of-memory results - and the Actions UI showed that
/// exactly as it shows a selection miss (issue #36). "Nothing was proved" and "a test was missed"
/// call for different responses, which is why <c>shadow</c> has always told them apart.
/// </remarks>
public sealed class VerifyExitCodeTests
{
    [Fact]
    public void A_run_with_usable_samples_and_no_miss_exits_zero()
    {
        Assert.Equal(ExitCodes.Ok, VerifyCommand.ExitCode(new MutationHarnessResult([Clean(1), Skipped(2)]), null));
    }

    [Fact]
    public void A_miss_exits_one()
    {
        var result = new MutationHarnessResult([Clean(1), Missed(2)]);

        Assert.Equal(ExitCodes.Failed, VerifyCommand.ExitCode(result, null));
    }

    [Fact]
    public void A_run_with_no_usable_sample_is_inconclusive_rather_than_failed()
    {
        // The shape of the 2026-10-20 nightly: every sample read as out of memory, so the run
        // observed nothing at all. That is not a miss, and must not be reported as one.
        var result = new MutationHarnessResult(
        [
            new MutationSample { Index = 1, Outcome = SampleOutcome.OutOfMemory, SkipReason = "heap limit" },
            new MutationSample { Index = 2, Outcome = SampleOutcome.TimedOut, SkipReason = "budget" },
            Skipped(3),
        ]);

        Assert.Equal(ExitCodes.Inconclusive, VerifyCommand.ExitCode(result, null));
    }

    [Fact]
    public void A_run_with_no_samples_at_all_is_inconclusive()
    {
        Assert.Equal(ExitCodes.Inconclusive, VerifyCommand.ExitCode(new MutationHarnessResult([]), null));
    }

    [Fact]
    public void More_unobserved_samples_than_allowed_exits_one()
    {
        // It is a defect in the suite rather than the selection, but it is one the caller asked to
        // be failed on, so it is a failure and not a "could not tell".
        var result = new MutationHarnessResult([Unobserved(1), Unobserved(2), Clean(3)]);

        Assert.Equal(ExitCodes.Failed, VerifyCommand.ExitCode(result, maxUnobserved: 1));
        Assert.Equal(ExitCodes.Ok, VerifyCommand.ExitCode(result, maxUnobserved: 2));
        Assert.Equal(ExitCodes.Ok, VerifyCommand.ExitCode(result, maxUnobserved: null));
    }

    [Fact]
    public void A_project_granularity_run_with_no_miss_exits_zero()
    {
        // Never reported as PASS, but nothing was found wrong by the means it had, and that has
        // always exited 0. Turning it into "inconclusive" would fail every repository that opted in.
        var result = new MutationHarnessResult([Clean(1)], ProjectGranularity: true);

        Assert.Equal(ExitCodes.Ok, VerifyCommand.ExitCode(result, null));
    }

    [Fact]
    public void A_refusal_to_start_is_inconclusive()
    {
        Assert.Equal(ExitCodes.Inconclusive, VerifyCommand.RefusalExitCode(new InvalidOperationException("the working tree is not clean")));
    }

    [Fact]
    public void A_run_abandoned_with_no_miss_found_is_inconclusive()
    {
        var abandoned = new MutationRunAbandonedException(
            "sample 2 did not restore Calculator.cs", new MutationHarnessResult([Clean(1), Clean(2)]));

        Assert.Equal(ExitCodes.Inconclusive, VerifyCommand.RefusalExitCode(abandoned));
    }

    [Fact]
    public void A_miss_found_before_the_run_was_abandoned_still_exits_one()
    {
        // The samples before the abandonment ran against the tree they meant to, so a miss among
        // them is real. Reporting it as "could not tell" would hide the defect the gate exists for.
        var abandoned = new MutationRunAbandonedException(
            "sample 2 did not restore Calculator.cs", new MutationHarnessResult([Missed(1), Clean(2)]));

        Assert.Equal(ExitCodes.Failed, VerifyCommand.RefusalExitCode(abandoned));
    }

    private static MutationSample Clean(int index) =>
        new() { Index = index, Outcome = SampleOutcome.Clean, FailingTests = 1 };

    private static MutationSample Unobserved(int index) =>
        new() { Index = index, Outcome = SampleOutcome.Clean, FailingTests = 0 };

    private static MutationSample Missed(int index) =>
        new() { Index = index, Outcome = SampleOutcome.Miss, FailingTests = 1, Misses = ["App.Tests.WidgetTests.Works"] };

    private static MutationSample Skipped(int index) =>
        new() { Index = index, Outcome = SampleOutcome.Skipped, SkipReason = "no mutation site" };
}

/// <summary>
/// Exit codes read from the commands themselves, invoked through the surface <c>Main</c> builds.
/// </summary>
/// <remarks>
/// A mapping function can be right and still be thrown away by its caller: <c>watch --run --once</c>
/// ran the selection, discarded the code <see cref="SelectiveRun.Execute"/> returned, and exited 0
/// on a red suite (issue #36). Only invoking the command sees that.
/// </remarks>
[Collection(nameof(FixtureCollection))]
public sealed class CommandExitCodeTests(XunitFixtureRepository repository) : IDisposable
{
    private const string Original = "public int Add(int a, int b) => a + b;";

    private const string Broken = "public int Add(int a, int b) => a - b;";

    public void Dispose() => repository.Revert();

    [Fact]
    public async Task Watch_once_run_exits_with_the_failure_of_the_tests_it_ran()
    {
        repository.Edit("Fixtures.Core/Calculator.cs", Original, Broken);

        var exitCode = await Cli.InvokeAsync(
            "watch", "--once", "--run", "--base", "HEAD", "--no-cache",
            "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath);

        Assert.Equal(ExitCodes.Failed, exitCode);
    }

    [Fact]
    public async Task Watch_once_run_with_nothing_impacted_exits_zero()
    {
        // The control: a clean tree selects nothing, so nothing runs and nothing failed. Without it
        // the test above would pass for a command that could not exit 0 at all.
        var exitCode = await Cli.InvokeAsync(
            "watch", "--once", "--run", "--base", "HEAD", "--no-cache",
            "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath);

        Assert.Equal(ExitCodes.Ok, exitCode);
    }

    [Fact]
    public async Task Verify_refusing_to_start_is_inconclusive_rather_than_a_miss()
    {
        // A dirty tree is the commonest refusal and the cheapest to provoke: the harness checks it
        // before it loads anything. Nothing was mutated, so nothing was missed.
        repository.Edit("Fixtures.Core/Calculator.cs", Original, Broken);

        var exitCode = await Cli.InvokeAsync(
            "verify", "--mutate", "1", "--path", repository.AnalysisRoot, "--solution", repository.SolutionPath);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }
}

/// <summary>Invokes the real command surface, as <c>Main</c> does.</summary>
internal static class Cli
{
    public static Task<int> InvokeAsync(params string[] arguments) =>
        Program.BuildRoot(new CommonOptions())
            .Parse(arguments)
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
}
