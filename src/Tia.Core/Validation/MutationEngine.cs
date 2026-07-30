using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tia.Core.Validation;

public sealed record Mutation
{
    public required string FilePath { get; init; }

    public required string OriginalText { get; init; }

    public required string MutatedText { get; init; }

    /// <summary>What was changed, for the harness report.</summary>
    public required string Description { get; init; }

    public required int Line { get; init; }

    public required string Member { get; init; }
}

/// <summary>
/// Stryker-style mutations, used to manufacture ground truth for the correctness harness.
/// </summary>
/// <remarks>
/// Replaying real commits produces almost no failing tests to check a selection against, because
/// real commits are almost all green. Injected mutations produce unlimited ground truth: mutate,
/// select against the mutation as the diff, run the full suite, and any test that fails but was
/// not selected is a miss.
/// </remarks>
public sealed class MutationEngine
{
    private static readonly Dictionary<SyntaxKind, SyntaxKind> OperatorSwaps = new()
    {
        [SyntaxKind.GreaterThanToken] = SyntaxKind.GreaterThanEqualsToken,
        [SyntaxKind.GreaterThanEqualsToken] = SyntaxKind.GreaterThanToken,
        [SyntaxKind.LessThanToken] = SyntaxKind.LessThanEqualsToken,
        [SyntaxKind.LessThanEqualsToken] = SyntaxKind.LessThanToken,
        [SyntaxKind.EqualsEqualsToken] = SyntaxKind.ExclamationEqualsToken,
        [SyntaxKind.ExclamationEqualsToken] = SyntaxKind.EqualsEqualsToken,
        [SyntaxKind.PlusToken] = SyntaxKind.MinusToken,
        [SyntaxKind.MinusToken] = SyntaxKind.PlusToken,
        [SyntaxKind.AmpersandAmpersandToken] = SyntaxKind.BarBarToken,
        [SyntaxKind.BarBarToken] = SyntaxKind.AmpersandAmpersandToken,
    };

    /// <summary>Applies one random mutation, or returns null when the file offers no site.</summary>
    public Mutation? TryMutate(string filePath, string sourceText, Random random)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText, path: filePath);
        var root = tree.GetRoot();

        var sites = new List<Func<Mutation?>>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case LiteralExpressionSyntax literal
                    when literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression):
                {
                    var captured = literal;
                    sites.Add(() => FlipLiteral(filePath, sourceText, root, captured));
                    break;
                }

                case BinaryExpressionSyntax binary when OperatorSwaps.ContainsKey(binary.OperatorToken.Kind()):
                {
                    var captured = binary;
                    sites.Add(() => SwapOperator(filePath, sourceText, root, captured));
                    break;
                }

                case LiteralExpressionSyntax numeric when numeric.IsKind(SyntaxKind.NumericLiteralExpression):
                {
                    var captured = numeric;
                    sites.Add(() => ShiftNumber(filePath, sourceText, root, captured));
                    break;
                }

                case LiteralExpressionSyntax text when text.IsKind(SyntaxKind.StringLiteralExpression)
                                                      || text.IsKind(SyntaxKind.CharacterLiteralExpression):
                {
                    var captured = text;
                    sites.Add(() => PerturbText(filePath, sourceText, root, captured));
                    break;
                }

                case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } access:
                {
                    var captured = access;
                    sites.Add(() => ShiftIndex(filePath, sourceText, root, captured));
                    break;
                }

                case BlockSyntax { Statements.Count: > 1 } block:
                {
                    var captured = block;
                    sites.Add(() => DropStatement(filePath, sourceText, root, captured, random));
                    break;
                }
            }
        }

        while (sites.Count > 0)
        {
            var index = random.Next(sites.Count);
            var mutation = sites[index]();
            if (mutation is not null)
            {
                return mutation;
            }

            sites.RemoveAt(index);
        }

        return null;
    }

    private static Mutation? FlipLiteral(string filePath, string sourceText, SyntaxNode root, LiteralExpressionSyntax literal)
    {
        var isTrue = literal.IsKind(SyntaxKind.TrueLiteralExpression);
        var replacement = SyntaxFactory
            .LiteralExpression(isTrue ? SyntaxKind.FalseLiteralExpression : SyntaxKind.TrueLiteralExpression)
            .WithTriviaFrom(literal);

        return Build(filePath, sourceText, root, literal, root.ReplaceNode(literal, replacement),
            $"{(isTrue ? "true" : "false")} -> {(isTrue ? "false" : "true")}");
    }

    private static Mutation? SwapOperator(string filePath, string sourceText, SyntaxNode root, BinaryExpressionSyntax binary)
    {
        var original = binary.OperatorToken;
        var swapped = SyntaxFactory.Token(original.LeadingTrivia, OperatorSwaps[original.Kind()], original.TrailingTrivia);
        var replacement = binary.WithOperatorToken(swapped);

        return Build(filePath, sourceText, root, binary, root.ReplaceNode(binary, replacement),
            $"'{original.ValueText}' -> '{swapped.ValueText}'");
    }

    private static Mutation? ShiftNumber(string filePath, string sourceText, SyntaxNode root, LiteralExpressionSyntax literal)
    {
        if (literal.Token.Value is not (int or long or short or byte) || literal.Token.Text.Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!int.TryParse(literal.Token.Text, out var value))
        {
            return null;
        }

        var replacement = SyntaxFactory
            .LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value + 1))
            .WithTriviaFrom(literal);

        return Build(filePath, sourceText, root, literal, root.ReplaceNode(literal, replacement), $"{value} -> {value + 1}");
    }

    private static Mutation? PerturbText(string filePath, string sourceText, SyntaxNode root, LiteralExpressionSyntax literal)
    {
        if (literal.Token.Value is not string original)
        {
            // A character literal: swapping it for one that cannot appear in ordinary text.
            return literal.Token.Value is char c
                ? Build(filePath, sourceText, root, literal,
                    root.ReplaceNode(literal, SyntaxFactory.LiteralExpression(
                        SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal(c == '§' ? '¶' : '§')).WithTriviaFrom(literal)),
                    $"'{c}' -> '§'")
                : null;
        }

        var mutated = original.Length == 0 ? "tia" : original + "§";
        var replacement = SyntaxFactory
            .LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(mutated))
            .WithTriviaFrom(literal);

        return Build(filePath, sourceText, root, literal, root.ReplaceNode(literal, replacement),
            $"\"{Truncate(original)}\" -> \"{Truncate(mutated)}\"");
    }

    private static Mutation? ShiftIndex(string filePath, string sourceText, SyntaxNode root, ElementAccessExpressionSyntax access)
    {
        var argument = access.ArgumentList.Arguments[0].Expression;

        // `x[^1]` becomes `x[^2]`, `x[0]` becomes `x[1]`: off-by-one indexing is a mutation that
        // survives compilation and changes behaviour, which is exactly what the harness needs.
        SyntaxNode replacement = argument switch
        {
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.IndexExpression, Operand: LiteralExpressionSyntax { Token.Value: int index } } =>
                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.IndexExpression,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(index + 1))),
            _ => null!,
        };

        return replacement is null
            ? null
            : Build(filePath, sourceText, root, argument, root.ReplaceNode(argument, replacement.WithTriviaFrom(argument)),
                $"index {argument} -> {replacement}");
    }

    private static Mutation? DropStatement(string filePath, string sourceText, SyntaxNode root, BlockSyntax block, Random random)
    {
        var candidates = block.Statements
            .Where(s => s is ExpressionStatementSyntax or LocalDeclarationStatementSyntax)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var statement = candidates[random.Next(candidates.Count)];

        // Removing a declaration whose variable is used later would not compile, so only
        // statements that are expressions are dropped.
        if (statement is not ExpressionStatementSyntax)
        {
            return null;
        }

        var mutated = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia);
        return mutated is null
            ? null
            : Build(filePath, sourceText, root, statement, mutated, $"dropped statement `{Truncate(statement.ToString())}`");
    }

    private static Mutation Build(string filePath, string sourceText, SyntaxNode root, SyntaxNode site, SyntaxNode mutatedRoot, string description)
    {
        var line = root.SyntaxTree.GetLineSpan(site.Span).StartLinePosition.Line + 1;
        var member = DescribeMember(site);

        return new Mutation
        {
            FilePath = filePath,
            OriginalText = sourceText,
            MutatedText = mutatedRoot.ToFullString(),
            Description = description,
            Line = line,
            Member = member,
        };
    }

    private static string DescribeMember(SyntaxNode site)
    {
        for (var node = site; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return $"{TypeNameOf(method)}.{method.Identifier.ValueText}";
                case PropertyDeclarationSyntax property:
                    return $"{TypeNameOf(property)}.{property.Identifier.ValueText}";
                case ConstructorDeclarationSyntax ctor:
                    return $"{TypeNameOf(ctor)}..ctor";
            }
        }

        return "<unknown>";
    }

    private static string TypeNameOf(SyntaxNode node) =>
        node.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>()?.Identifier.ValueText ?? "<global>";

    private static string Truncate(string text) =>
        text.Length <= 48 ? text.Replace('\n', ' ') : text[..45].Replace('\n', ' ') + "...";
}
