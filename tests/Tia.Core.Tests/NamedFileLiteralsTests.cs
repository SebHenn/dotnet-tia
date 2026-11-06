using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Safety;

namespace Tia.Core.Tests;

public sealed class NamedFileLiteralsTests
{
    [Theory]
    [InlineData("schema.sql")]
    [InlineData("SCHEMA.SQL")]
    [InlineData("db/schema.sql")]
    [InlineData(@"db\schema.sql")]
    [InlineData("./db/schema.sql")]
    [InlineData("../../db/schema.sql")]
    [InlineData("/db/schema.sql")]
    [InlineData("C:/repo/db/schema.sql")]
    public void A_literal_ending_in_the_path_names_the_file(string literal) =>
        Assert.True(NamedFileLiterals.Names(literal, "db/schema.sql"));

    [Theory]
    [InlineData("")]
    [InlineData("db")]
    [InlineData("schema")]
    [InlineData("my-schema.sql")]
    [InlineData("schema.sql.bak")]
    [InlineData("other/schema.sql")]
    [InlineData("see db/schema.sql")]
    [InlineData("https://example.com/db/schema.sql")]
    public void Anything_else_does_not(string literal) =>
        Assert.False(NamedFileLiterals.Names(literal, "db/schema.sql"));

    [Fact]
    public void A_file_without_an_extension_is_named_like_any_other() =>
        Assert.True(NamedFileLiterals.Names("Dockerfile", "build/Dockerfile"));

    [Fact]
    public void Every_kind_of_string_is_read_and_reports_the_line_it_is_on()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                string A => "one.txt";
                string B => @"dir\two.txt";
                string C2 => $"{A}/three.txt";
                string D => "not-a-match.txt";
                string E => /* comment.txt */ "x";
            }
            """);

        var lines = NamedFileLiterals.Find(tree, ["one.txt", "dir/two.txt", "three.txt", "comment.txt"]);

        Assert.Equal([3, 4, 5], lines.Select(l => l.Line).Order());
        Assert.Equal("dir/two.txt", lines.Single(l => l.Line == 4).Path);
    }

    [Fact]
    public void A_raw_string_is_read()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { string A => \"\"\"raw.txt\"\"\"; }");

        Assert.Single(NamedFileLiterals.Find(tree, ["raw.txt"]));
    }

    [Fact]
    public void Disabled_code_names_nothing()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
            #if NEVER
                string A => "off.txt";
            #endif
            }
            """);

        Assert.Empty(NamedFileLiterals.Find(tree, ["off.txt"]));
    }
}
