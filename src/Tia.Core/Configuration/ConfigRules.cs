using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Reporting;

namespace Tia.Core.Configuration;

/// <summary>What a diff becomes under <c>tia.json</c>'s path rules (#170).</summary>
/// <param name="FullRunReasons">One per changed file that forces a full run.</param>
/// <param name="Diff">The diff without the files <c>ignorePaths</c> left out.</param>
/// <param name="Ignored">Every file left out, and the pattern that left it out.</param>
public sealed record ConfiguredDiff(IReadOnlyList<string> FullRunReasons, DiffResult Diff, IReadOnlyList<IgnoredFile> Ignored);

/// <summary>Applies a <see cref="TiaConfig"/> to a diff and to a selection.</summary>
public static class ConfigRules
{
    /// <summary>
    /// The diff under the config's path rules.
    /// </summary>
    /// <param name="configDirectory">
    /// Where <c>tia.json</c> sits, relative to the repository root with forward slashes, or empty
    /// for the root. The diff's paths are relative to the repository root and the patterns to this.
    /// </param>
    /// <remarks>
    /// <para>
    /// A change to <c>tia.json</c> itself runs everything, because it changes what selection means:
    /// a pattern added to <c>ignorePaths</c> in the same pull request as the files it ignores would
    /// otherwise approve itself.
    /// </para>
    /// <para>
    /// A full-run path beats an ignored one, and a rename is ignored only when both of its paths
    /// are: a file moved out of <c>docs/</c> into a project is a change to that project.
    /// </para>
    /// </remarks>
    public static ConfiguredDiff Apply(TiaConfig config, string configDirectory, DiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(configDirectory);
        ArgumentNullException.ThrowIfNull(diff);

        var prefix = configDirectory.Length == 0 ? string.Empty : configDirectory.TrimEnd('/') + "/";
        var configFile = prefix + TiaConfig.FileName;

        var reasons = new List<string>();
        var kept = new List<ChangedFile>();
        var ignored = new List<IgnoredFile>();

        foreach (var file in diff.Files)
        {
            string[] paths = file.OldPath is { } old && old != file.Path ? [file.Path, old] : [file.Path];

            if (paths.Contains(configFile, StringComparer.Ordinal))
            {
                reasons.Add($"{configFile} changed - it decides what selection means, so this change is not narrowed by it");
                kept.Add(file);
                continue;
            }

            var local = paths.Select(p => Local(prefix, p)).ToList();

            if (local.SelectMany(p => p is null ? [] : config.FullRunPaths.Where(pattern => pattern.Matches(p)))
                    .FirstOrDefault() is { } fullRun)
            {
                reasons.Add($"{file.Path} changed - it matches '{fullRun.Text}' in fullRunPaths of {configFile}");
                kept.Add(file);
                continue;
            }

            var patterns = local.Select(p => p is null ? null : config.IgnorePaths.FirstOrDefault(pattern => pattern.Matches(p))).ToList();
            if (patterns.All(p => p is not null))
            {
                ignored.Add(new IgnoredFile { Path = file.Path, Pattern = patterns[0]!.Text });
                continue;
            }

            kept.Add(file);
        }

        return new ConfiguredDiff(reasons, ignored.Count == 0 ? diff : diff with { Files = kept }, ignored);
    }

    /// <summary>
    /// The tests <c>alwaysRun</c> names that <paramref name="selected"/> does not already hold, in
    /// name order, so they run after everything the change reached.
    /// </summary>
    public static IReadOnlyList<TestMethod> AlwaysRun(TiaConfig config, IReadOnlyList<TestMethod> allTests, IReadOnlyList<TestMethod> selected)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(allTests);
        ArgumentNullException.ThrowIfNull(selected);

        if (config.AlwaysRun.Count == 0)
        {
            return [];
        }

        var already = selected.ToHashSet();

        return
        [
            .. allTests
                .Where(t => !already.Contains(t) &&
                            config.AlwaysRun.Any(p => p.Matches(t.FullyQualifiedName)))
                .OrderBy(t => t.FullyQualifiedName, StringComparer.Ordinal),
        ];
    }

    /// <summary>The <c>alwaysRun</c> patterns that name no test at all, which is almost always a typo.</summary>
    public static IEnumerable<string> Unmatched(TiaConfig config, IReadOnlyList<TestMethod> allTests)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(allTests);

        return config.AlwaysRun
            .Where(p => !allTests.Any(t => p.Matches(t.FullyQualifiedName)))
            .Select(p => p.Text);
    }

    private static string? Local(string prefix, string path) =>
        prefix.Length == 0 ? path : path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : null;
}
