using Tia.Core.Model;
using Tia.Frameworks;
using Tia.Frameworks.Dialects;

namespace Tia.Core.Tests;

public sealed class FilterDialectTests
{
    [Fact]
    public void VsTest_ors_contains_matches()
    {
        var selected = new[] { Test("App", "WidgetTests", "A"), Test("App", "WidgetTests", "B") };
        var all = new[] { selected[0], selected[1], Test("App", "WidgetTests", "C") };

        var arguments = new VsTestFilterDialect().BuildArguments(selected, all);

        Assert.Equal(["--filter", "FullyQualifiedName~App.WidgetTests.A|FullyQualifiedName~App.WidgetTests.B"], arguments);
    }

    [Fact]
    public void VsTest_escapes_the_operator_characters()
    {
        Assert.Equal(@"App.Widget\(x\).A", VsTestFilterDialect.Escape("App.Widget(x).A"));
    }

    [Fact]
    public void VsTest_reports_the_tests_a_contains_match_pulls_in()
    {
        var selected = new[] { Test("App", "WidgetTests", "Add") };
        var all = new[] { selected[0], Test("App", "WidgetTests", "AddRange"), Test("App", "WidgetTests", "Remove") };

        var extra = new VsTestFilterDialect().ExtraMatches(selected, all);

        Assert.Equal("App.WidgetTests.AddRange", Assert.Single(extra).FullyQualifiedName);
    }

    [Fact]
    public void Xunit_v3_repeats_filter_method_and_never_emits_vstest_syntax()
    {
        var selected = new[] { Test("App", "WidgetTests", "A"), Test("App", "WidgetTests", "B") };
        var all = new[] { selected[0], selected[1], Test("App", "WidgetTests", "C") };

        var arguments = new XunitV3MtpDialect().BuildArguments(selected, all);

        Assert.Equal(["--filter-method", "App.WidgetTests.A", "--filter-method", "App.WidgetTests.B"], arguments);
        Assert.DoesNotContain("--filter", arguments);
    }

    [Fact]
    public void TUnit_emits_an_exact_path_for_a_single_class()
    {
        var arguments = new TUnitTreeNodeDialect().BuildArguments([Test("App", "WidgetTests", "A"), Test("App", "WidgetTests", "B")], []);

        Assert.Equal(["--treenode-filter", "/*/App/WidgetTests/A|B"], arguments);
    }

    [Fact]
    public void TUnit_reports_the_cross_product_it_has_to_emit_across_classes()
    {
        // The grammar alternates within a segment, not across whole paths, so two classes with two
        // method names describe four cells. That is a superset, never a subset - and it is reported.
        var selected = new[] { Test("App", "AlphaTests", "One"), Test("App", "BetaTests", "Two") };
        var all = new[] { selected[0], selected[1], Test("App", "AlphaTests", "Two") };

        var dialect = new TUnitTreeNodeDialect();

        Assert.Equal(["--treenode-filter", "/*/App/AlphaTests|BetaTests/One|Two"], dialect.BuildArguments(selected, all));
        Assert.Equal("App.AlphaTests.Two", Assert.Single(dialect.ExtraMatches(selected, all)).FullyQualifiedName);
    }

    [Theory]
    [InlineData(TestFramework.XUnitV2, TestRunner.VsTest, "vstest")]
    [InlineData(TestFramework.XUnitV3, TestRunner.MicrosoftTestingPlatform, "xunit-v3-mtp")]
    [InlineData(TestFramework.XUnitV3, TestRunner.VsTest, "vstest")]
    [InlineData(TestFramework.NUnit, TestRunner.MicrosoftTestingPlatform, "vstest")]
    [InlineData(TestFramework.MSTest, TestRunner.VsTest, "vstest")]
    [InlineData(TestFramework.TUnit, TestRunner.MicrosoftTestingPlatform, "tunit-treenode")]
    public void Each_framework_and_runner_pair_picks_its_dialect(TestFramework framework, TestRunner runner, string expected)
    {
        Assert.Equal(expected, FilterDialects.For(framework, runner).Name);
    }

    [Fact]
    public void A_selection_covering_most_of_a_project_runs_unfiltered()
    {
        var all = Enumerable.Range(0, 10).Select(i => Test("App", "T", "M" + i)).ToList();
        var selected = all.Take(8).ToList();

        var plan = FilterPlanner.Plan(new VsTestFilterDialect(), selected, all);

        Assert.False(plan.Filtered);
        Assert.Contains("covers", plan.UnfilteredReason);
    }

    [Fact]
    public void A_filter_longer_than_the_command_line_limit_is_dropped()
    {
        var all = Enumerable.Range(0, 500).Select(i => Test("App", "VeryLongTestClassNameToPadTheFilter", "Method" + i)).ToList();
        var selected = all.Take(100).ToList();

        var plan = FilterPlanner.Plan(new VsTestFilterDialect(), selected, all, maxFilterLength: 200);

        Assert.False(plan.Filtered);
        Assert.Contains("command-line limit", plan.UnfilteredReason);
    }

    [Fact]
    public void A_small_selection_is_filtered()
    {
        var all = Enumerable.Range(0, 100).Select(i => Test("App", "T", "M" + i)).ToList();

        var plan = FilterPlanner.Plan(new VsTestFilterDialect(), all.Take(3).ToList(), all);

        Assert.True(plan.Filtered);
        Assert.Equal("--filter", plan.Arguments[0]);
    }

    [Fact]
    public void A_class_whose_tests_are_all_selected_collapses_to_one_clause()
    {
        // Filters are abandoned once they outgrow the command line, so length is correctness-
        // adjacent: a filter that does not fit means the whole project runs.
        var all = Enumerable.Range(0, 40).Select(i => Test("App", "WidgetTests", "Method" + i)).ToList();

        var arguments = new VsTestFilterDialect().BuildArguments(all, all);

        Assert.Equal(["--filter", "FullyQualifiedName~App.WidgetTests."], arguments);
    }

    [Fact]
    public void A_partially_selected_class_still_names_each_test()
    {
        var all = Enumerable.Range(0, 4).Select(i => Test("App", "WidgetTests", "Method" + i)).ToList();

        var arguments = new VsTestFilterDialect().BuildArguments(all.Take(2).ToList(), all);

        Assert.Equal(
            ["--filter", "FullyQualifiedName~App.WidgetTests.Method0|FullyQualifiedName~App.WidgetTests.Method1"],
            arguments);
    }

    [Fact]
    public void Xunit_v3_collapses_to_classes_only_when_that_expresses_the_whole_selection()
    {
        var alpha = Enumerable.Range(0, 3).Select(i => Test("App", "AlphaTests", "M" + i)).ToList();
        var beta = Enumerable.Range(0, 3).Select(i => Test("App", "BetaTests", "M" + i)).ToList();
        var all = alpha.Concat(beta).ToList();

        var arguments = new XunitV3MtpDialect().BuildArguments([.. alpha, .. beta], all);

        Assert.Equal(["--filter-class", "App.AlphaTests", "--filter-class", "App.BetaTests"], arguments);
    }

    [Fact]
    public void Xunit_v3_never_mixes_class_and_method_filters()
    {
        // Same-kind filters are OR-ed but two different kinds are AND-ed, so a mixture means
        // "tests in AlphaTests that are also BetaTests.M0" - which is nothing at all. This is not
        // visible in the argument list; it took running the real runner to find.
        var alpha = Enumerable.Range(0, 3).Select(i => Test("App", "AlphaTests", "M" + i)).ToList();
        var beta = Enumerable.Range(0, 3).Select(i => Test("App", "BetaTests", "M" + i)).ToList();

        var arguments = new XunitV3MtpDialect().BuildArguments([.. alpha, beta[0]], [.. alpha, .. beta]);

        Assert.DoesNotContain("--filter-class", arguments);
        Assert.Equal(4, arguments.Count(a => a == "--filter-method"));
    }

    [Fact]
    public void Collapsing_makes_a_long_filter_fit()
    {
        // The case measured on NodaTime: enough selected tests that the filter was abandoned.
        var all = Enumerable.Range(0, 60)
            .SelectMany(c => Enumerable.Range(0, 20).Select(m => Test("NodaTime.Test", "SomeFairlyLongTestClassName" + c, "AnEquallyLongTestMethodName" + m)))
            .ToList();

        var plan = FilterPlanner.Plan(new VsTestFilterDialect(), all, all, coverageThreshold: 2.0);

        Assert.True(plan.Filtered);
        Assert.True(plan.Arguments[1].Length < 4000, $"filter was {plan.Arguments[1].Length} characters");
    }

    private static TestMethod Test(string ns, string className, string method) => new()
    {
        SymbolKey = $"asm|M:{ns}.{className}.{method}",
        ClassKey = $"asm|T:{ns}.{className}",
        Namespace = ns,
        ClassName = className,
        MethodName = method,
        ProjectName = "App.Tests",
        Framework = TestFramework.XUnitV2,
    };
}
