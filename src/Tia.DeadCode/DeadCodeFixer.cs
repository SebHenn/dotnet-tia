using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Tia.DeadCode;

/// <param name="Files">New text for each file to rewrite, keyed by its path relative to the scanned directory.</param>
/// <param name="Deleted">The findings the rewrite removes.</param>
/// <param name="Skipped">Findings left in place, each with why.</param>
public sealed record FixPlan(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<Finding> Deleted,
    IReadOnlyList<(Finding Finding, string Reason)> Skipped);

/// <summary>Plans the text edits that delete findings the oracle found safe.</summary>
/// <remarks>
/// Every refusal here leaves a declaration in place, so it can only make the fix smaller, never
/// wrong: a partial type (its parts are in several files), a declarator that shares its
/// declaration with others, a declaration whose surrounding trivia holds a preprocessor directive
/// (removing one half of an <c>#if</c> pair breaks the file), and a file the edit would leave with a
/// syntax error. What remains is checked once more by a real build in the command.
/// </remarks>
public static class DeadCodeFixer
{
    public static FixPlan Plan(string root, IReadOnlyList<OracleProject> projects, IReadOnlyList<Finding> findings, CancellationToken cancellationToken = default)
    {
        var skipped = new List<(Finding, string)>();
        var deleted = new List<Finding>();
        var edits = new Dictionary<string, (SourceText Text, List<(TextSpan Span, Finding Finding)> Spans)>(StringComparer.OrdinalIgnoreCase);

        foreach (var finding in findings)
        {
            if (edits.TryGetValue(finding.File, out var seen) && seen.Spans.Any(entry => entry.Finding == finding))
            {
                continue;
            }

            SyntaxNode? declaration = null;
            SyntaxTree? tree = null;
            foreach (var project in projects)
            {
                tree = project.Compilation.SyntaxTrees.FirstOrDefault(candidate =>
                    string.Equals(Relative(root, candidate.FilePath), finding.File, StringComparison.OrdinalIgnoreCase));
                if (tree is not null && DeletionOracle.Declaration(tree, finding, cancellationToken) is { } found)
                {
                    declaration = found;
                    break;
                }
            }

            if (declaration is null || tree is null)
            {
                skipped.Add((finding, "its declaration was not found in the source"));
                continue;
            }

            if (Refusal(declaration) is { } reason)
            {
                skipped.Add((finding, reason));
                continue;
            }

            var target = declaration is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: { } field } } ? field : declaration;
            if (!edits.TryGetValue(finding.File, out var entry))
            {
                entry = (tree.GetText(cancellationToken), []);
                edits[finding.File] = entry;
            }

            entry.Spans.Add((target.FullSpan, finding));
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, (text, spans)) in edits)
        {
            // A declaration inside another one that is going anyway is covered by the outer one.
            var outer = spans.Where(span => !spans.Any(other => other.Span != span.Span && other.Span.Contains(span.Span))).DistinctBy(span => span.Span).ToList();
            var edited = text.WithChanges(outer.Select(span => new TextChange(span.Span, string.Empty)));
            if (CSharpSyntaxTree.ParseText(edited.ToString(), cancellationToken: cancellationToken).GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                skipped.AddRange(spans.Select(span => (span.Finding, "deleting it would leave the file with a syntax error")));
                continue;
            }

            files[file] = edited.ToString();
            deleted.AddRange(spans.Select(span => span.Finding));
        }

        return new FixPlan(files, deleted, skipped);
    }

    private static string? Refusal(SyntaxNode declaration)
    {
        if (declaration is TypeDeclarationSyntax { Modifiers: var modifiers } && modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return "it is a partial type, whose parts are not all in one place";
        }

        if (declaration is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: > 1 } })
        {
            return "its declaration declares several fields";
        }

        if (declaration is EnumMemberDeclarationSyntax)
        {
            return "it is an enum member, whose value others may depend on";
        }

        var target = declaration is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: { } field } } ? field : declaration;
        return target.GetLeadingTrivia().Concat(target.GetTrailingTrivia()).Any(trivia => trivia.IsDirective)
            ? "a preprocessor directive sits next to it"
            : null;
    }

    private static string Relative(string root, string path) =>
        path.Length == 0 ? path : Path.GetRelativePath(root, path).Replace('\\', '/');
}
