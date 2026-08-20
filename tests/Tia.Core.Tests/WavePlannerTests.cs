using Tia.Core.Model;
using Tia.Frameworks;
using Tia.Frameworks.Dialects;

namespace Tia.Core.Tests;

/// <summary>
/// Dividing a ranked selection into a nearest slice and the rest.
/// </summary>
/// <remarks>
/// The failure modes here are all green ones. A test that runs twice passes twice, and a pair of
/// filters that between them match one extra test still reports success - so neither is visible to
/// the mutation gate, which only ever asks whether something was missed. These are the checks that
/// stand in for it.
/// </remarks>
public sealed class WavePlannerTests
{
    private static TestMethod Test(string className, string method, string ns = "App") => new()
    {
        SymbolKey = $"asm|M:{ns}.{className}.{method}",
        ClassKey = $"asm|T:{ns}.{className}",
        Namespace = ns,
        ClassName = className,
        MethodName = method,
        ProjectName = "App.Tests",
        Framework = TestFramework.XUnitV2,
    };

    /// <summary>Four classes of four, in rank order: the wave should take the nearest one.</summary>
    private static List<TestMethod> Ranked(int classes = 4, int perClass = 4) =>
        [.. Enumerable.Range(0, classes).SelectMany(c =>
            Enumerable.Range(0, perClass).Select(m => Test($"Class{c}Tests", $"Test{m}")))];

    [Fact]
    public void The_first_wave_is_the_nearest_class()
    {
        var ranked = Ranked();

        var plan = WavePlanner.Plan(new VsTestFilterDialect(), ranked, ranked);

        Assert.True(plan.Split);
        Assert.Equal(4, plan.FirstWaveTests);
        Assert.Contains("Class0Tests", plan.FirstWaveArguments[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Class1Tests", plan.FirstWaveArguments[1], StringComparison.Ordinal);
        Assert.Contains("Class1Tests", plan.RemainderArguments[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Class0Tests", plan.RemainderArguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_class_is_never_split_across_the_two_waves()
    {
        // Ten classes of three: a quarter of thirty is 8, which lands mid-class. Rounding up to the
        // class boundary is what keeps the two filters from matching into each other.
        var ranked = Ranked(classes: 10, perClass: 3);

        var plan = WavePlanner.Plan(new VsTestFilterDialect(), ranked, ranked);

        Assert.True(plan.Split);
        Assert.Equal(0, plan.FirstWaveTests % 3);
        Assert.Equal(9, plan.FirstWaveTests);
    }

    [Fact]
    public void A_selection_too_small_to_pay_for_a_second_invocation_is_not_divided()
    {
        var ranked = Ranked(classes: 3, perClass: 2);

        var plan = WavePlanner.Plan(new VsTestFilterDialect(), ranked, ranked);

        Assert.False(plan.Split);
        Assert.Contains("below the", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void One_class_cannot_be_divided()
    {
        var ranked = Ranked(classes: 1, perClass: 12);

        var plan = WavePlanner.Plan(new VsTestFilterDialect(), ranked, ranked);

        Assert.False(plan.Split);
        Assert.Contains("one class", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_first_wave_that_would_be_most_of_the_run_is_refused()
    {
        // The nearest class holds nine of the twelve selected tests, so running it first is running
        // the selection - and paying a second invocation for the three that are left.
        List<TestMethod> ranked =
        [
            .. Enumerable.Range(0, 9).Select(m => Test("BigTests", $"Test{m}")),
            .. Enumerable.Range(0, 3).Select(m => Test("SmallTests", $"Test{m}")),
        ];

        var plan = WavePlanner.Plan(new VsTestFilterDialect(), ranked, ranked);

        Assert.False(plan.Split);
        Assert.Contains("most of the run", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filter_that_would_not_fit_on_the_command_line_is_refused()
    {
        var ranked = Ranked(classes: 8, perClass: 4);

        var plan = WavePlanner.Plan(new VsTestFilterDialect(), ranked, ranked, maxFilterLength: 40);

        Assert.False(plan.Split);
        Assert.Contains("command-line limit", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hazard a class boundary cannot rule out: a nested class, whose name has its outer
    /// class's name as a prefix. <c>FullyQualifiedName~App.Alpha.</c> is a contains-match, so a
    /// wave holding <c>Alpha</c> also matches every test of <c>Alpha.Nested</c> in the remainder -
    /// and both would run them.
    /// </summary>
    [Fact]
    public void Waves_whose_filters_would_run_a_test_twice_are_refused()
    {
        List<TestMethod> ranked =
        [
            .. Enumerable.Range(0, 4).Select(m => Test("Alpha", $"Test{m}")),
            .. Enumerable.Range(0, 8).Select(m => Test("Alpha.Nested", $"Test{m}")),
        ];

        var plan = WavePlanner.Plan(new VsTestFilterDialect(), ranked, ranked);

        Assert.False(plan.Split);
        Assert.Contains("twice", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property that makes this safe to do at all: two invocations may run fewer tests than one
    /// would have, never more. Checked over every dialect, because each collapses classes
    /// differently and the argument for the property runs through exactly that.
    /// </summary>
    [Theory]
    [InlineData("vstest")]
    [InlineData("xunit-v3-mtp")]
    [InlineData("tunit-treenode")]
    public void Dividing_never_runs_a_test_one_filter_would_not_have(string dialectName)
    {
        IFilterDialect dialect = dialectName switch
        {
            "xunit-v3-mtp" => new XunitV3MtpDialect(),
            "tunit-treenode" => new TUnitTreeNodeDialect(),
            _ => new VsTestFilterDialect(),
        };

        var ranked = Ranked(classes: 5, perClass: 4);
        var all = new List<TestMethod>(ranked) { Test("Class0Tests", "Unselected"), Test("OtherTests", "Untouched") };

        var plan = WavePlanner.Plan(dialect, ranked, all);
        if (!plan.Split)
        {
            // Refusing is always allowed. What is not allowed is splitting and matching more.
            return;
        }

        var wave = ranked.Take(plan.FirstWaveTests).ToList();
        var remainder = ranked.Skip(plan.FirstWaveTests).ToList();

        var single = Matched(dialect, ranked, all);
        var divided = Matched(dialect, wave, all);
        divided.UnionWith(Matched(dialect, remainder, all));

        Assert.Subset(single, divided);
    }

    [Fact]
    public void Every_selected_test_is_in_exactly_one_wave()
    {
        var ranked = Ranked(classes: 5, perClass: 4);

        var plan = WavePlanner.Plan(new VsTestFilterDialect(), ranked, ranked);

        Assert.True(plan.Split);

        var wave = Matched(new VsTestFilterDialect(), ranked.Take(plan.FirstWaveTests).ToList(), ranked);
        var remainder = Matched(new VsTestFilterDialect(), ranked.Skip(plan.FirstWaveTests).ToList(), ranked);

        Assert.Empty(wave.Intersect(remainder, StringComparer.Ordinal));

        var union = new HashSet<string>(wave, StringComparer.Ordinal);
        union.UnionWith(remainder);
        Assert.Equal(ranked.Count, union.Count);
    }

    /// <summary>What a dialect's filter matches: the selection plus whatever it admits to pulling in.</summary>
    private static HashSet<string> Matched(
        IFilterDialect dialect,
        IReadOnlyList<TestMethod> selected,
        IReadOnlyList<TestMethod> all)
    {
        var matched = new HashSet<string>(selected.Select(t => t.FullyQualifiedName), StringComparer.Ordinal);
        matched.UnionWith(dialect.ExtraMatches(selected, all).Select(t => t.FullyQualifiedName));
        return matched;
    }
}
