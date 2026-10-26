using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Tia.Core.Reporting;
using Tia.Workspace;
using Tia.Workspace.Harness;

namespace Tia.Cli.Commands;

/// <summary>
/// The correctness harness. Injects a mutation, selects against it, then runs the whole suite:
/// any test that fails but was not selected is a miss, and a miss is the only fatal defect class
/// this tool has. Zero misses is the merge gate.
/// </summary>
public static class VerifyCommand
{
    public static Command Create(CommonOptions common)
    {
        var mutate = new Option<int>("--mutate")
        {
            Description = "Number of mutation samples to run.",
            DefaultValueFactory = _ => 25,
        };

        var seed = new Option<int>("--seed")
        {
            Description = "Random seed, so a failing run can be replayed.",
            DefaultValueFactory = _ => Environment.TickCount,
        };

        var projectGranularity = new Option<bool>("--project-granularity")
        {
            Description =
                "Gate a repository whose test projects cannot write TRX, by reading each project's " +
                "exit code instead of individual test outcomes. Can find a miss; can never confirm " +
                "there is none, and never reports PASS.",
        };

        // A limit on the suite, not the selection, and off by default so that turning it on is a
        // decision. A nightly that wants to know when its tests stop noticing changes can gate on
        // it; a repository with a thin suite is not told it failed a check it never asked for.
        var maxUnobserved = new Option<int?>("--max-unobserved")
        {
            Description =
                "Fail when more than this many usable samples broke no test at all. Measures the " +
                "suite rather than the selection, so it is off unless given.",
            CustomParser = result => CommonOptions.ParseBounded<int>(
                result,
                "--max-unobserved",
                token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null,
                value => value >= 0,
                "a whole number, zero or more"),
        };

        var command = new Command("verify", "Prove the selection never misses a failing test, by mutation.");

        // The harness writes its own mutation and diffs the working tree, so there is no base
        // revision to take and forcing a full run would defeat the point of the check.
        common.AddTo(command, common.Base, common.Full, common.DefaultBranch);
        command.Options.Add(mutate);
        command.Options.Add(seed);
        command.Options.Add(projectGranularity);
        command.Options.Add(maxUnobserved);

        command.SetAction(SolutionChoice.Refusing(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(common.Json);
            var unobservedLimit = parseResult.GetValue(maxUnobserved);
            var options = common.Read(
                parseResult, parseResult.GetValue(common.Verbose) && !json ? Console.Error.WriteLine : null);

            // Before the harness, which starts by asking git about the working tree: in a directory
            // that does not exist that question is a crash rather than an answer (#50).
            SetupException.RequireRepository(options.RepositoryRoot);

            var harness = new MutationHarness(
                options,
                json ? null : message => Console.Out.WriteLine("  " + message),
                notice: message => Console.Error.WriteLine("  " + message));

            // Resolved once and printed: the default is the clock, so a run that found a miss could
            // not be replayed unless something said which seed it had drawn (#39).
            var seedValue = parseResult.GetValue(seed);

            if (!json)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine($"  seed {seedValue} - pass --seed {seedValue} to draw the same mutations again");
            }

            MutationHarnessResult result;

            try
            {
                result = await harness
                    .RunAsync(
                        parseResult.GetValue(mutate),
                        new Random(seedValue),
                        parseResult.GetValue(projectGranularity),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // Everything this harness refuses to start on - a dirty working tree, a solution
                // with no test projects - is something the caller can act on. Printing a stack
                // trace at them buries the sentence that says what to do, and an unhandled
                // exception is indistinguishable from the tool having broken. A working tree the
                // harness cannot vouch for is not a 1 either: it has not found anything wrong with
                // the selection or the repository; it does not know what state the working tree is
                // in, and says so the way every other "nothing could be concluded" is said (#38).
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Cannot run the harness: " + ex.Message);
                Console.Error.WriteLine();

                if (ex is MutationRunAbandonedException { Partial.Misses: > 0 } abandoned)
                {
                    foreach (var sample in abandoned.Partial.Samples.Where(s => s.Misses.Count > 0))
                    {
                        Console.Error.WriteLine($"    [{sample.Index}] MISS  {sample.Misses.Count} failing test(s) were not selected:");
                        foreach (var miss in sample.Misses.Take(10))
                        {
                            Console.Error.WriteLine($"            - {miss}");
                        }
                    }

                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  FAIL - a failing test was not selected before the run was abandoned.");
                    Console.Error.WriteLine();
                }

                return RefusalExitCode(ex);
            }

            var unobserved = result.Unobserved;
            var overLimit = unobservedLimit is { } limit && unobserved.Count > limit;
            var exitCode = ExitCode(result, unobservedLimit);

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = AnalysisReport.SchemaVersion,
                        Seed = seedValue,
                        result.Passed,
                        result.ProjectGranularity,
                        result.FoundNoMiss,
                        result.Usable,
                        result.Skipped,
                        result.Misses,
                        // The sample indices, not a count: the count is the headline, and the
                        // indices are what a reader needs next to find which mutations they were.
                        Unobserved = unobserved.Select(s => s.Index),
                        MaxUnobserved = unobservedLimit,
                        // Named, not numbered. SampleOutcome would serialise as its ordinal, and
                        // "outcome": 2 tells a consumer nothing about whether it was checked.
                        Samples = result.Samples.Select(s => new
                        {
                            s.Index,
                            Outcome = s.Outcome.ToString(),
                            s.Member,
                            s.Description,
                            s.File,
                            s.Line,
                            s.FailingTests,
                            s.SelectedTests,
                            s.TotalTests,
                            s.Misses,
                            s.FirstFailurePosition,
                            s.OrderedTests,
                            s.SkipReason,
                        }),
                    },
                    AnalysisReport.JsonOptions));
                return exitCode;
            }

            foreach (var sample in result.Samples)
            {
                switch (sample.Outcome)
                {
                    // OK is true of these about selection and misleading about the suite: nothing
                    // failed, so there was nothing for the selection to miss.
                    case SampleOutcome.Clean when sample.FailingTests == 0:
                        Console.Out.WriteLine($"    [{sample.Index}] UNOBSERVED  no test failed " +
                                              $"({sample.SelectedTests}/{sample.TotalTests} selected)");
                        break;

                    case SampleOutcome.Clean:
                        Console.Out.WriteLine($"    [{sample.Index}] OK    {sample.FailingTests} failing test(s), all selected " +
                                              $"({sample.SelectedTests}/{sample.TotalTests} selected)");
                        break;

                    case SampleOutcome.Miss:
                        Console.Out.WriteLine($"    [{sample.Index}] MISS  {sample.Misses.Count} failing test(s) were not selected:");
                        foreach (var miss in sample.Misses.Take(10))
                        {
                            Console.Out.WriteLine($"            - {miss}");
                        }

                        break;

                    // Not "skip". A sample that hung had something to check and the harness could
                    // not finish checking it, which is worth a word that makes a reader look.
                    case SampleOutcome.TimedOut:
                        Console.Out.WriteLine($"    [{sample.Index}] TIME  {sample.SkipReason}");
                        break;

                    case SampleOutcome.OutOfMemory:
                        Console.Out.WriteLine($"    [{sample.Index}] MEM   {sample.SkipReason}");
                        break;

                    default:
                        Console.Out.WriteLine($"    [{sample.Index}] skip  {sample.SkipReason}");
                        break;
                }
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine($"  {result.Usable} usable sample(s), {result.Skipped} skipped, {result.Misses} miss(es)"
                                  + (result.TimedOut > 0 ? $", {result.TimedOut} timed out" : string.Empty)
                                  + (result.OutOfMemory > 0 ? $", {result.OutOfMemory} ran out of memory" : string.Empty));

            if (unobserved.Count > 0)
            {
                Console.Out.WriteLine($"  {unobserved.Count} of {result.Usable} usable mutation(s) were observed by no test  " +
                                      $"({string.Join(" ", unobserved.Select(s => $"[{s.Index}]"))})");
            }

            // Not part of the gate. Ordering skips nothing, so it cannot cause the defect this
            // command exists to find; it is reported here because this is the only place that
            // knows both which tests a change broke and which order they would have run in.
            if (result.TimeToFirstFailure is { } ttff)
            {
                Console.Out.WriteLine(FormattableString.Invariant(
                    $"  First failure at {ttff.Ordered:P0} of the ordered run, against {ttff.Unordered:P0} expected unordered ({ttff.Samples} sample(s) with a failure in the selection)."));
            }

            // When most samples skip, the count alone reads like a footnote and the run looks
            // stronger than it is. The commonest cause is a working tree that already contains
            // unrelated changes - a modified project file is a full-run trigger, so every sample
            // bails out and proves nothing - and naming it turns a puzzling number into an
            // instruction.
            if (result.Skipped > result.Usable)
            {
                var reasons = result.Samples
                    .Where(s => s.SkipReason is { Length: > 0 })
                    .GroupBy(s => s.SkipReason!, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count());

                Console.Out.WriteLine("  Most samples were skipped:");
                foreach (var reason in reasons.Take(3))
                {
                    Console.Out.WriteLine($"    {reason.Count()}x  {reason.Key}");
                }

                // A pointer at the reasons above rather than a guess at one of them. The reasons
                // used to be dropped, so this line named the commonest cause as though it were the
                // observed one - and on a run whose samples all bailed for an unrelated reason it
                // sent the reader to a `git status` that was clean.
                Console.Out.WriteLine("    Each reason above is the analysis's own. If one names a file the harness did not");
                Console.Out.WriteLine("    touch, check `git status`: the working tree is diffed as it stands, so changes it");
                Console.Out.WriteLine("    did not make are analysed too, and a modified project file forces a full run.");
            }
            Console.Out.WriteLine(result switch
            {
                { Passed: true } => "  PASS - no failing test was left out of a selection.",
                { Usable: 0 } => "  INCONCLUSIVE - no sample could be checked, so nothing was proved.",
                // Deliberately not PASS, and deliberately not a synonym for it. This run saw which
                // projects failed, not which tests, so it can say no project failed wholly outside
                // the selection - and cannot say that no test did.
                { ProjectGranularity: true, FoundNoMiss: true } =>
                    "  PROJECT-GRANULARITY GATE - no project failed entirely outside a selection. Individual\n" +
                    "  test outcomes were not readable, so this rules out one shape of miss and not the rest.",
                _ => "  FAIL - a failing test was not selected. This is the one defect class that matters.",
            });

            // A second line, not a replacement for the first: the verdict on selection stands
            // either way, and a reader should not have to work out which of the two checks failed.
            if (overLimit)
            {
                Console.Out.WriteLine(
                    $"  FAIL - {unobserved.Count} mutation(s) were observed by no test, more than --max-unobserved {unobservedLimit} allows.\n" +
                    "  This is the suite failing to notice a change, not the selection missing a test.");
            }

            Console.Out.WriteLine();

            return exitCode;
        }));

        return command;
    }

    /// <summary>
    /// <see cref="ExitCodes.Failed"/> for a defect found - a miss, or more unobserved mutations
    /// than <c>--max-unobserved</c> allows - and <see cref="ExitCodes.Inconclusive"/> when no sample
    /// could be checked.
    /// </summary>
    /// <remarks>
    /// Both used to be 1. On 2026-10-20 the gate on tia itself had 0 usable samples, 14 of them
    /// false out-of-memory results, and the Actions UI showed it exactly as it shows a selection
    /// miss (issue #36). The two call for different responses: a miss is a graph lacking an edge,
    /// and zero usable samples is a harness that could not observe anything. <c>shadow</c> has
    /// always told them apart this way. The checks cannot disagree, so their order is not a
    /// priority: a miss and an unobserved sample are both usable samples.
    /// </remarks>
    public static int ExitCode(MutationHarnessResult result, int? maxUnobserved)
    {
        if (result.Misses > 0)
        {
            return ExitCodes.Failed;
        }

        if (result.Usable == 0)
        {
            return ExitCodes.Inconclusive;
        }

        return maxUnobserved is { } limit && result.Unobserved.Count > limit
            ? ExitCodes.Failed
            : ExitCodes.Ok;
    }

    /// <summary>
    /// <see cref="ExitCodes.Inconclusive"/> for a run the harness would not start or would not
    /// finish, unless a sample it finished had already found a miss.
    /// </summary>
    /// <remarks>
    /// A refusal to start - a dirty tree, no test projects, a runner that cannot write TRX - checked
    /// nothing, so it missed nothing; it used to exit 1 and read as a miss (issue #36). A run
    /// abandoned part-way is different only when a sample before the abandonment saw a miss: that
    /// sample ran against the tree it meant to, so its miss is real, and calling it "could not tell"
    /// would hide the one defect this command exists to find.
    /// </remarks>
    public static int RefusalExitCode(InvalidOperationException refusal) =>
        refusal is MutationRunAbandonedException { Partial.Misses: > 0 }
            ? ExitCodes.Failed
            : ExitCodes.Inconclusive;
}
