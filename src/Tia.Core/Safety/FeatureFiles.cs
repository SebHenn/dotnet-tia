using System.Text.RegularExpressions;

namespace Tia.Core.Safety;

/// <summary>
/// Decides whether a Reqnroll or SpecFlow <c>.feature</c> file and the test class generated from it
/// still describe the same scenarios, so that changing the feature selects that class and not every
/// test in the project (#172).
/// </summary>
/// <remarks>
/// <para>
/// The generated <c>X.feature.cs</c> sits beside <c>X.feature</c>, is named the same plus
/// <c>.cs</c>, and is rebuilt from the feature, so the class a feature becomes is read from the
/// compilation rather than derived from the title by naming rules this tool would have to copy.
/// What the compilation holds, though, is the class as of the last build: a scenario added since
/// has no method in it, and a class selected by what the graph knows would miss it.
/// </para>
/// <para>
/// Why this cannot cause a miss: <see cref="Matches"/> is true only when the feature's one title and
/// its complete set of scenario names equal the title and the scenario names the generated class
/// carries as <c>FeatureTitle</c> and <c>Description</c> traits. Anything that makes the two
/// unequal - a scenario added, removed or renamed, a title change, another Gherkin language, a
/// name this reader does not understand, a runner whose generated code carries no such traits -
/// returns false and the caller widens the project as it did before. A scenario that is only
/// edited keeps its name and its method, which the class selection already contains.
/// </para>
/// </remarks>
public static partial class FeatureFiles
{
    [GeneratedRegex(@"^\s*#\s*language\s*:", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageComment();

    [GeneratedRegex(@"^\s*Feature:[ \t]*(?<name>[^\r\n]*?)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FeatureLine();

    [GeneratedRegex(@"^\s*(?:Scenario Outline|Scenario Template|Scenario|Example):[ \t]*(?<name>[^\r\n]*?)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ScenarioLine();

    [GeneratedRegex(@"TraitAttribute\(""FeatureTitle"",\s*""(?<value>[^""]*)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex TitleTrait();

    [GeneratedRegex(@"TraitAttribute\(""Description"",\s*""(?<value>[^""]*)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex DescriptionTrait();

    /// <summary>
    /// True when <paramref name="featureText"/> and <paramref name="generatedSource"/> agree on one
    /// feature title and on every scenario name, in the xUnit form Reqnroll generates.
    /// </summary>
    public static bool Matches(string featureText, string generatedSource)
    {
        ArgumentNullException.ThrowIfNull(featureText);
        ArgumentNullException.ThrowIfNull(generatedSource);

        if (LanguageComment().IsMatch(featureText) || ContainsEscape(featureText, generatedSource))
        {
            return false;
        }

        var titles = FeatureLine().Matches(featureText);
        var generatedTitles = TitleTrait().Matches(generatedSource).Select(m => m.Groups["value"].Value).Distinct(StringComparer.Ordinal).ToList();

        if (titles.Count != 1 || generatedTitles.Count != 1 ||
            !string.Equals(titles[0].Groups["name"].Value, generatedTitles[0], StringComparison.Ordinal))
        {
            return false;
        }

        var scenarios = ScenarioLine().Matches(featureText).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        var generated = DescriptionTrait().Matches(generatedSource).Select(m => m.Groups["value"].Value).ToHashSet(StringComparer.Ordinal);

        return scenarios.Count > 0 && scenarios.SetEquals(generated);
    }

    /// <summary>A name with a backslash may be an escape this reader would compare wrongly; widen.</summary>
    private static bool ContainsEscape(string featureText, string generatedSource) =>
        TitleTrait().Matches(generatedSource).Concat(DescriptionTrait().Matches(generatedSource))
            .Any(m => m.Groups["value"].Value.Contains('\\', StringComparison.Ordinal)) ||
        ScenarioLine().Matches(featureText).Any(m => m.Groups["name"].Value.Contains('\\', StringComparison.Ordinal));

    /// <summary>
    /// True for source that may read a feature file itself: it names the extension in any case, spells
    /// "feature" as a string, or reads manifest resources (Reqnroll embeds every feature file in the
    /// assembly). Text matched; a false match only widens.
    /// </summary>
    public static bool ReadsFeatureFiles(string sourceText) => ReaderPattern().IsMatch(sourceText);

    [GeneratedRegex(@"\.feature(?![A-Za-z0-9_])|""feature""|ManifestResource", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReaderPattern();
}
