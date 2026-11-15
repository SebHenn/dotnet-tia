using Tia.Core.Safety;

namespace Tia.Core.Tests;

public sealed class FeatureFilesTests
{
    private const string Feature = "Feature: Calculator add\n\n  Scenario: Add two numbers\n    Given a\n\n  Scenario Outline: Add many\n    Given b\n";

    private static string Generated(string title, params string[] scenarios) =>
        string.Concat(scenarios.Select(s =>
            $"[Xunit.TraitAttribute(\"FeatureTitle\", \"{title}\")]\n[Xunit.TraitAttribute(\"Description\", \"{s}\")]\npublic void M() {{}}\n"));

    [Fact]
    public void A_feature_and_its_generated_class_that_agree_match()
    {
        Assert.True(FeatureFiles.Matches(Feature, Generated("Calculator add", "Add two numbers", "Add many")));
    }

    [Fact]
    public void A_scenario_added_since_the_last_build_does_not_match()
    {
        Assert.False(FeatureFiles.Matches(Feature + "\n  Scenario: Brand new\n    Given c\n", Generated("Calculator add", "Add two numbers", "Add many")));
    }

    [Fact]
    public void A_scenario_renamed_or_removed_does_not_match()
    {
        Assert.False(FeatureFiles.Matches(Feature.Replace("Add two numbers", "Sum", StringComparison.Ordinal), Generated("Calculator add", "Add two numbers", "Add many")));
        Assert.False(FeatureFiles.Matches(Feature, Generated("Calculator add", "Add two numbers")));
    }

    [Fact]
    public void A_changed_title_does_not_match()
    {
        Assert.False(FeatureFiles.Matches(Feature.Replace("Calculator add", "Calculator sum", StringComparison.Ordinal), Generated("Calculator add", "Add two numbers", "Add many")));
    }

    [Fact]
    public void Another_gherkin_language_does_not_match()
    {
        Assert.False(FeatureFiles.Matches("# language: de\n" + Feature, Generated("Calculator add", "Add two numbers", "Add many")));
    }

    [Fact]
    public void A_name_with_a_backslash_does_not_match()
    {
        Assert.False(FeatureFiles.Matches("Feature: F\n  Scenario: a\\b\n", Generated("F", "a\\\\b")));
    }

    [Fact]
    public void Generated_code_without_the_traits_does_not_match()
    {
        Assert.False(FeatureFiles.Matches(Feature, "public class C { public void M() {} }"));
    }

    [Fact]
    public void A_feature_without_scenarios_does_not_match()
    {
        Assert.False(FeatureFiles.Matches("Feature: Empty\n", Generated("Empty")));
    }

    [Theory]
    [InlineData("File.ReadAllText(\"Calc.feature\")", true)]
    [InlineData("var x = \"a.feature\";", true)]
    [InlineData("var x = Foo.FeatureFlag;", false)]
    [InlineData("var x = obj.features;", false)]
    [InlineData("asm.GetManifestResourceNames()", true)]
    [InlineData("Directory.GetFiles(dir, \"*.FEATURE\")", true)]
    [InlineData("Path.ChangeExtension(p, \"feature\")", true)]
    public void A_source_that_names_a_feature_file_reads_them(string source, bool reads)
    {
        Assert.Equal(reads, FeatureFiles.ReadsFeatureFiles(source));
    }
}
