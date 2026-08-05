using System.Text;
using Tia.Core.Validation;

namespace Tia.Core.Tests;

/// <summary>
/// The mutation harness edits real files in a real repository and puts them back, so "puts them
/// back" has to mean byte-for-byte. It did not, and the way it failed was invisible.
/// </summary>
public sealed class SourceFileTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public void A_file_with_a_byte_order_mark_round_trips_exactly()
    {
        var bytes = Bytes("class C { }", withBom: true);

        Assert.Equal(bytes, SourceFile.FromBytes(bytes).Bytes);
    }

    [Fact]
    public void A_file_without_a_byte_order_mark_round_trips_exactly()
    {
        var bytes = Bytes("class C { }", withBom: false);

        Assert.Equal(bytes, SourceFile.FromBytes(bytes).Bytes);
    }

    [Fact]
    public void The_mark_is_not_part_of_the_text()
    {
        Assert.Equal("class C { }", SourceFile.FromBytes(Bytes("class C { }", withBom: true)).Text);
    }

    [Fact]
    public void Rewriting_keeps_the_mark()
    {
        // Without this, every mutated file also reports line 1 as changed, and the diff the
        // harness measures is not the diff it injected.
        var source = SourceFile.FromBytes(Bytes("class C { }", withBom: true));

        Assert.Equal(Bytes("class D { }", withBom: true), source.Rewrite("class D { }"));
    }

    [Fact]
    public void Rewriting_does_not_add_a_mark_that_was_not_there()
    {
        var source = SourceFile.FromBytes(Bytes("class C { }", withBom: false));

        Assert.Equal(Bytes("class D { }", withBom: false), source.Rewrite("class D { }"));
    }

    [Fact]
    public void Rewriting_the_same_text_is_a_no_op()
    {
        var bytes = Bytes("class C { }", withBom: true);

        Assert.Equal(bytes, SourceFile.FromBytes(bytes).Rewrite("class C { }"));
    }

    [Fact]
    public void A_file_shorter_than_a_mark_is_read_as_text()
    {
        Assert.Equal("//", SourceFile.FromBytes(Encoding.UTF8.GetBytes("//")).Text);
    }

    [Fact]
    public void An_empty_file_round_trips()
    {
        Assert.Equal([], SourceFile.FromBytes([]).Bytes);
        Assert.Equal(string.Empty, SourceFile.FromBytes([]).Text);
    }

    [Fact]
    public void Non_ascii_content_survives_the_round_trip()
    {
        var bytes = Bytes("var s = \"Zürich – 東京\";", withBom: true);

        Assert.Equal(bytes, SourceFile.FromBytes(bytes).Bytes);
        Assert.Equal("var s = \"Zürich – 東京\";", SourceFile.FromBytes(bytes).Text);
    }

    private static byte[] Bytes(string text, bool withBom) =>
        withBom ? [.. Bom, .. Encoding.UTF8.GetBytes(text)] : Encoding.UTF8.GetBytes(text);
}
