using Tia.Core.Infrastructure;
using Tia.Workspace.Harness;

namespace Tia.Integration.Tests;

/// <summary>
/// The gate's own logic.
/// </summary>
/// <remarks>
/// Every correctness claim this project makes rests on the mutation harness, and until now the
/// harness itself had no tests at all. A gate that is wrong does not fail loudly - it reports PASS,
/// which is indistinguishable from working and makes every number derived from it worthless.
/// </remarks>
public sealed class MutationHarnessTests
{
    private static readonly HashSet<string> Selected = new(StringComparer.Ordinal)
    {
        "App.Tests.CounterTests.Increments",
        "App.Tests.WidgetTests.Cases",
    };

    [Fact]
    public void A_failing_test_in_the_selection_is_not_a_miss()
    {
        Assert.True(MutationHarness.IsSelected("App.Tests.CounterTests.Increments", Selected));
    }

    [Fact]
    public void A_failing_test_outside_the_selection_is_a_miss()
    {
        Assert.False(MutationHarness.IsSelected("App.Tests.CounterTests.Decrements", Selected));
    }

    [Fact]
    public void A_same_named_test_in_a_different_class_is_a_miss()
    {
        // The defect this test exists for. The comparison was a symmetric suffix match, so a
        // failing Other.CounterTests.Increments matched the selected App.Tests.CounterTests
        // .Increments and the sample was recorded Clean - a miss reported as a pass.
        Assert.False(MutationHarness.IsSelected("Other.CounterTests.Increments", Selected));
    }

    [Fact]
    public void A_test_whose_name_merely_ends_with_a_selected_name_is_a_miss()
    {
        Assert.False(MutationHarness.IsSelected("Prefixed.App.Tests.CounterTests.Increments", Selected));
    }

    [Fact]
    public void A_data_case_matches_the_method_it_belongs_to()
    {
        // Parameterised tests are selected whole, so the selection names the method while the
        // runner reports each case.
        Assert.True(MutationHarness.IsSelected("""App.Tests.WidgetTests.Cases(1, "a")""", Selected));
    }

    [Fact]
    public void A_data_case_of_an_unselected_method_is_still_a_miss()
    {
        Assert.False(MutationHarness.IsSelected("App.Tests.WidgetTests.Other(1)", Selected));
    }

    [Fact]
    public void A_run_that_observed_nothing_does_not_pass()
    {
        // "Zero misses" out of zero observations is not a pass. A run where every sample was
        // skipped must not look like a run that saw everything and found nothing wrong - that is
        // how a gate silently stops gating.
        var result = new MutationHarnessResult([Skipped(1), Skipped(2)]);

        Assert.Equal(0, result.Usable);
        Assert.Equal(0, result.Misses);
        Assert.False(result.Passed);
    }

    [Fact]
    public void A_run_with_usable_samples_and_no_misses_passes()
    {
        var result = new MutationHarnessResult([Clean(1), Skipped(2), Clean(3)]);

        Assert.Equal(2, result.Usable);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.Passed);
    }

    [Fact]
    public void A_single_miss_fails_the_run()
    {
        var result = new MutationHarnessResult([Clean(1), Missed(2, "App.Tests.WidgetTests.Works")]);

        Assert.Equal(1, result.Misses);
        Assert.False(result.Passed);
    }

    [Fact]
    public void A_sample_that_does_not_restore_a_file_is_detected()
    {
        // The guard added after a real incident: the harness stripped a byte-order mark on read
        // and never wrote it back, so every later sample analysed that leftover change too. The
        // diff grew with each sample, the selection grew with it, and a gate that selects more and
        // more cannot find a miss - while still printing PASS.
        var directory = Directory.CreateTempSubdirectory("tia-drift-");

        try
        {
            var file = Path.Combine(directory.FullName, "Widget.cs");
            File.WriteAllText(file, "class Widget { }");
            var before = MutationHarness.Fingerprint([file]);

            Assert.Null(MutationHarness.Drift([file], before));

            // A single byte, the shape the real defect took.
            File.WriteAllText(file, "﻿class Widget { }");
            Assert.Equal(file, MutationHarness.Drift([file], before));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_harness_refuses_to_start_against_a_working_tree_that_is_not_clean()
    {
        // Same failure as the drift guard above, arriving from the other direction: the drift check
        // catches a change the harness made and did not undo, this catches one that was already
        // there. Both end as a selection that grows sample by sample until no miss can be detected,
        // and both print PASS while doing it. The replay harness has refused a dirty tree since it
        // was written; the mutation harness mutates files in place and did not.
        var directory = Directory.CreateTempSubdirectory("tia-clean-");

        try
        {
            var root = directory.FullName;
            var token = TestContext.Current.CancellationToken;

            Git(root, token, "init", "--initial-branch=main");
            Git(root, token, "config", "user.email", "tia@example.invalid");
            Git(root, token, "config", "user.name", "tia tests");

            File.WriteAllText(Path.Combine(root, "Widget.cs"), "class Widget { }");
            Git(root, token, "add", "-A");
            Git(root, token, "commit", "-m", "widget");

            // Committed: nothing for a sample's diff to pick up but its own mutation.
            MutationHarness.RequireCleanWorkingTree(root);

            File.WriteAllText(Path.Combine(root, "Widget.cs"), "class Widget { int x; }");

            var modified = Assert.Throws<InvalidOperationException>(
                () => MutationHarness.RequireCleanWorkingTree(root));
            Assert.Contains("Widget.cs", modified.Message, StringComparison.Ordinal);

            Git(root, token, "checkout", "--", "Widget.cs");
            MutationHarness.RequireCleanWorkingTree(root);

            // Untracked counts too, unlike the replay harness: DiffResolver deliberately adds
            // untracked files to the diff, so one inflates a sample exactly as a modified file does.
            File.WriteAllText(Path.Combine(root, "Extra.cs"), "class Extra { }");

            var untracked = Assert.Throws<InvalidOperationException>(
                () => MutationHarness.RequireCleanWorkingTree(root));
            Assert.Contains("Extra.cs", untracked.Message, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory.FullName, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            directory.Delete(recursive: true);
        }
    }

    private static void Git(string root, CancellationToken cancellationToken, params string[] args) =>
        ProcessRunner.Run("git", args, root, cancellationToken: cancellationToken);

    [Fact]
    public void A_deleted_file_counts_as_drift()
    {
        var directory = Directory.CreateTempSubdirectory("tia-drift-");

        try
        {
            var file = Path.Combine(directory.FullName, "Widget.cs");
            File.WriteAllText(file, "class Widget { }");
            var before = MutationHarness.Fingerprint([file]);

            File.Delete(file);
            Assert.Equal(file, MutationHarness.Drift([file], before));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static MutationSample Clean(int index) => new() { Index = index, Outcome = SampleOutcome.Clean };

    private static MutationSample Skipped(int index) => new()
    {
        Index = index,
        Outcome = SampleOutcome.Skipped,
        SkipReason = "no mutation site",
    };

    private static MutationSample Missed(int index, params string[] misses) => new()
    {
        Index = index,
        Outcome = SampleOutcome.Miss,
        Misses = misses,
    };
}
