using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tia.Core.Analysis;

/// <summary>
/// Recognises a change that cannot have moved a symbol, because it did not move a token.
/// </summary>
/// <remarks>
/// <para>
/// Comments, whitespace, blank lines and formatting are trivia: the compiler attaches them to
/// tokens and then binds the tokens. Two revisions of a file with the same token sequence produce
/// the same symbols and the same references, so a diff that only moved trivia has nothing to seed
/// - and without this, reindenting a file or updating its licence header selects every test that
/// touches it, which for a header at the top of a class is every test of that class.
/// </para>
/// <para>
/// Tokens, not text, is the whole point. A comparison that ignored "whitespace" textually would
/// call a change inside a raw string literal cosmetic, and that changes behaviour. A token
/// sequence is exactly what does not: token text carries string contents, so the literal moves.
/// </para>
/// <para>
/// The one input this cannot be decided without is the project's real parse options. Conditional
/// directives are trivia, and the code they exclude is trivia too - so a change inside a
/// <c>#if DEBUG</c> block is invisible to a parse that does not define <c>DEBUG</c>. Parsing with
/// the options the project actually compiles under makes that correct rather than lucky: code the
/// compiler excludes genuinely cannot affect this compilation, and another target framework that
/// includes it is a separate project with its own options and its own pass over the same file.
/// </para>
/// </remarks>
public static class TriviaOnlyChange
{
    /// <summary>
    /// True when both revisions parse to the same token sequence, so nothing the compiler binds
    /// has changed.
    /// </summary>
    public static bool Applies(string oldText, string newText, ParseOptions? parseOptions, CancellationToken cancellationToken = default)
    {
        // Cheap and decisive: identical text is trivially trivia-only, and differing lengths say
        // nothing either way, so neither shortcut is worth taking beyond the first.
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return true;
        }

        var options = parseOptions as CSharpParseOptions;

        var oldRoot = CSharpSyntaxTree.ParseText(oldText, options, cancellationToken: cancellationToken).GetRoot(cancellationToken);
        var newRoot = CSharpSyntaxTree.ParseText(newText, options, cancellationToken: cancellationToken).GetRoot(cancellationToken);

        using var oldTokens = oldRoot.DescendantTokens().GetEnumerator();
        using var newTokens = newRoot.DescendantTokens().GetEnumerator();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasOld = oldTokens.MoveNext();
            var hasNew = newTokens.MoveNext();

            // Defensive, and unreachable as this stands - which is worth saying, because the
            // mutation gate flips it to `true` and no test can fail. Both streams end with the
            // compilation unit's EndOfFileToken, so a length difference always shows up as a kind
            // mismatch below first: at the shorter side's EOF the longer side still has real code.
            // The guard stays because it is only unreachable while the enumeration includes EOF.
            if (hasOld != hasNew)
            {
                return false;
            }

            if (!hasOld)
            {
                return true;
            }

            if (oldTokens.Current.RawKind != newTokens.Current.RawKind ||
                !string.Equals(oldTokens.Current.Text, newTokens.Current.Text, StringComparison.Ordinal))
            {
                return false;
            }
        }
    }
}
