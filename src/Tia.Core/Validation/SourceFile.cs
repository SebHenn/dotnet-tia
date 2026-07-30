using System.Text;

namespace Tia.Core.Validation;

/// <summary>
/// A source file's bytes, its decoded text, and the ability to put either back exactly.
/// </summary>
/// <remarks>
/// The mutation harness edits real files in a real repository and restores them afterwards, which
/// makes "exactly" load-bearing. <c>File.ReadAllText</c> strips a UTF-8 byte-order mark and
/// <c>File.WriteAllText</c> does not put it back, so restoring a BOM'd file left it one byte short
/// of what git has - and the harness walked away having modified the repository it was validating.
/// The damage also accumulated: every later sample saw those files as changed too, so the diff
/// grew with each sample and the selection grew with it. A gate that selects more and more is a
/// gate that can no longer find a miss, and nothing about its output would have said so.
/// </remarks>
public sealed class SourceFile
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private SourceFile(byte[] bytes, bool hasPreamble, string text)
    {
        Bytes = bytes;
        HasPreamble = hasPreamble;
        Text = text;
    }

    /// <summary>The file exactly as it was read. Writing this back is always a no-op change.</summary>
    public byte[] Bytes { get; }

    public bool HasPreamble { get; }

    /// <summary>The decoded content, without any byte-order mark.</summary>
    public string Text { get; }

    public static SourceFile FromBytes(byte[] bytes)
    {
        var hasPreamble = bytes.Length >= Utf8Bom.Length &&
                          bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2];

        var offset = hasPreamble ? Utf8Bom.Length : 0;
        return new SourceFile(bytes, hasPreamble, Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset));
    }

    public static SourceFile Read(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>
    /// New content in the file's own encoding. The mark goes back on so that the only line a diff
    /// reports as changed is the one that actually changed - without it, line 1 changes too.
    /// </summary>
    public byte[] Rewrite(string text) =>
        HasPreamble ? [.. Utf8Bom, .. Encoding.UTF8.GetBytes(text)] : Encoding.UTF8.GetBytes(text);
}
