using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// Skipping a file is the one decision in the engine that can only ever cause a miss, so what
/// counts as "no token moved" is pinned in both directions.
/// </summary>
public sealed class TriviaOnlyChangeTests
{
    [Fact]
    public void A_comment_is_trivia()
    {
        Assert.True(TriviaOnlyChange.Applies(
            "class C { int F() => 1; }",
            "class C { /* explains F */ int F() => 1; }",
            null));
    }

    [Fact]
    public void Reindenting_is_trivia()
    {
        Assert.True(TriviaOnlyChange.Applies(
            "class C{int F()=>1;}",
            "class C\n{\n    int F() => 1;\n}\n",
            null));
    }

    [Fact]
    public void A_doc_comment_is_trivia()
    {
        Assert.True(TriviaOnlyChange.Applies(
            "class C { int F() => 1; }",
            "class C { /// <summary>One.</summary>\n int F() => 1; }",
            null));
    }

    [Fact]
    public void A_changed_body_is_not_trivia()
    {
        Assert.False(TriviaOnlyChange.Applies(
            "class C { int F() => 1; }",
            "class C { int F() => 2; }",
            null));
    }

    [Fact]
    public void Whitespace_inside_a_string_is_not_trivia()
    {
        // The reason this compares tokens rather than normalised text: inside a literal,
        // whitespace is content, and a comparison that treated it as formatting would skip a
        // file whose behaviour changed.
        Assert.False(TriviaOnlyChange.Applies(
            """class C { string F() => "a b"; }""",
            """class C { string F() => "a  b"; }""",
            null));
    }

    [Fact]
    public void Whitespace_inside_a_raw_string_is_not_trivia()
    {
        Assert.False(TriviaOnlyChange.Applies(
            "class C { string F() => \"\"\"\n    a b\n    \"\"\"; }",
            "class C { string F() => \"\"\"\n    a  b\n    \"\"\"; }",
            null));
    }

    [Fact]
    public void A_renamed_member_is_not_trivia()
    {
        Assert.False(TriviaOnlyChange.Applies(
            "class C { int F() => 1; }",
            "class C { int G() => 1; }",
            null));
    }

    [Fact]
    public void A_change_inside_an_enabled_conditional_block_is_not_trivia()
    {
        // Conditional directives are trivia and so is the code they exclude, so this verdict
        // depends entirely on being handed the options the project really compiles under.
        var withDebug = CSharpParseOptions.Default.WithPreprocessorSymbols("DEBUG");

        Assert.False(TriviaOnlyChange.Applies(
            "class C {\n#if DEBUG\n int F() => 1;\n#endif\n}",
            "class C {\n#if DEBUG\n int F() => 2;\n#endif\n}",
            withDebug));
    }

    [Fact]
    public void A_change_inside_an_excluded_conditional_block_is_trivia()
    {
        // The compiler does not see it, so nothing in this compilation can depend on it. Another
        // target framework that defines the symbol is a separate project, parsed separately.
        Assert.True(TriviaOnlyChange.Applies(
            "class C {\n#if DEBUG\n int F() => 1;\n#endif\n}",
            "class C {\n#if DEBUG\n int F() => 2;\n#endif\n}",
            CSharpParseOptions.Default));
    }

    [Fact]
    public void Flipping_a_conditional_is_not_trivia()
    {
        Assert.False(TriviaOnlyChange.Applies(
            "class C {\n#if DEBUG\n int F() => 1;\n#endif\n}",
            "class C {\n#if !DEBUG\n int F() => 1;\n#endif\n}",
            CSharpParseOptions.Default));
    }

    [Fact]
    public void An_added_member_is_not_trivia()
    {
        Assert.False(TriviaOnlyChange.Applies(
            "class C { int F() => 1; }",
            "class C { int F() => 1; int G() => 2; }",
            null));
    }

    [Fact]
    public void Identical_text_is_trivia()
    {
        Assert.True(TriviaOnlyChange.Applies("class C { }", "class C { }", null));
    }
}
