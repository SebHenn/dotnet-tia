using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tia.DeadCode;

/// <summary>One project file, its text, and every framework copy of its compilation.</summary>
public sealed record ResourceProject(string Path, string Text, IReadOnlyList<Compilation> Compilations);

/// <summary>An <c>EmbeddedResource</c> the project file lists and no source names.</summary>
public sealed record UnusedResource(string Project, string Include);

/// <summary>
/// Finds <c>EmbeddedResource</c> items nothing in the source names.
/// </summary>
/// <remarks>
/// A resource is read by its manifest name, which ends in the file name, so a string literal in
/// any compilation that contains the file name counts as naming it. Each of these only drops a
/// report: only items listed one by one are judged (a glob or conditioned item is not an
/// accident); <c>.resx</c> files are never judged (generated code reaches them without a string);
/// an item with a <c>LogicalName</c> is judged by that name; a project whose own source calls
/// <c>GetManifestResourceNames</c> enumerates its resources and is not judged; and the literals
/// of every project count, because another assembly can read this one's resources.
/// </remarks>
public static partial class UnusedResources
{
    public static IReadOnlyList<UnusedResource> Find(IReadOnlyList<ResourceProject> projects, CancellationToken cancellationToken = default)
    {
        var literals = new List<string>();
        foreach (var project in projects)
        {
            foreach (var compilation in project.Compilations)
            {
                foreach (var tree in compilation.SyntaxTrees)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    literals.AddRange(tree.GetRoot(cancellationToken).DescendantTokens()
                        .Where(token => token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken
                            or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.SingleLineRawStringLiteralToken
                            or SyntaxKind.MultiLineRawStringLiteralToken)
                        .Select(token => token.ValueText));
                }
            }
        }

        var found = new List<UnusedResource>();
        foreach (var project in projects)
        {
            if (project.Compilations.Any(Enumerates))
            {
                continue;
            }

            foreach (Match item in EmbeddedResourceElement().Matches(project.Text))
            {
                var element = item.Value;
                var listed = Attribute(element, "Include");
                if (listed is null)
                {
                    continue;
                }

                // An Include can list several files, separated by semicolons; each is judged alone.
                foreach (var include in listed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Judge(project, element, include, literals, found);
                }
            }
        }

        return found;
    }

    private static void Judge(ResourceProject project, string element, string include, List<string> literals, List<UnusedResource> found)
    {
        if (include.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)
            || include.Contains('*', StringComparison.Ordinal)
            || include.Contains('$', StringComparison.Ordinal)
            || element.Contains("Condition=", StringComparison.OrdinalIgnoreCase)
            || element.Contains("Remove=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var name = Attribute(element, "LogicalName") ?? include[(include.LastIndexOfAny(['/', '\\']) + 1)..];
        // The manifest name turns a space in a path into an underscore.
        var alternative = name.Replace(' ', '_');
        if (!literals.Any(literal => literal.Contains(name, StringComparison.OrdinalIgnoreCase)
            || literal.Contains(alternative, StringComparison.OrdinalIgnoreCase)))
        {
            found.Add(new UnusedResource(project.Path, include));
        }
    }

    private static bool Enumerates(Compilation compilation) =>
        compilation.SyntaxTrees.Any(tree => tree.GetText().ToString().Contains("GetManifestResourceNames", StringComparison.Ordinal));

    private static string? Attribute(string element, string name)
    {
        var match = Regex.Match(element, name + @"\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["value"].Value : null;
    }

    [GeneratedRegex(@"<EmbeddedResource\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedResourceElement();
}
