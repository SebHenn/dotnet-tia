using System.CommandLine;
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

        var command = new Command("verify", "Prove the selection never misses a failing test, by mutation.");
        common.AddTo(command);
        command.Options.Add(mutate);
        command.Options.Add(seed);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = common.Read(parseResult, parseResult.GetValue(common.Verbose) ? Console.Error.WriteLine : null);
            var harness = new MutationHarness(options, message => Console.Out.WriteLine("  " + message));

            Console.Out.WriteLine();
            var result = await harness
                .RunAsync(parseResult.GetValue(mutate), new Random(parseResult.GetValue(seed)), cancellationToken)
                .ConfigureAwait(false);

            foreach (var sample in result.Samples)
            {
                switch (sample.Outcome)
                {
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

                    default:
                        Console.Out.WriteLine($"    [{sample.Index}] skip  {sample.SkipReason}");
                        break;
                }
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine($"  {result.Usable} usable sample(s), {result.Skipped} skipped, {result.Misses} miss(es)");

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

                Console.Out.WriteLine("    Check `git status` - the harness diffs the working tree, so changes it did not");
                Console.Out.WriteLine("    make are analysed too, and a modified project file forces a full run.");
            }
            Console.Out.WriteLine(result switch
            {
                { Passed: true } => "  PASS - no failing test was left out of a selection.",
                { Usable: 0 } => "  INCONCLUSIVE - no sample could be checked, so nothing was proved.",
                _ => "  FAIL - a failing test was not selected. This is the one defect class that matters.",
            });
            Console.Out.WriteLine();

            return result.Passed ? 0 : 1;
        });

        return command;
    }
}
