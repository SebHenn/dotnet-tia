namespace Tia.Core.Model;

/// <summary>
/// Where one declaration sits in one file, so a changed line range can be mapped onto the symbol
/// it declares without producing a compilation.
/// </summary>
/// <remarks>
/// <para>
/// Recorded while the graph is built, when a semantic model is in hand anyway, and stored with the
/// fragment. That is what makes it trustworthy: a fragment is only reused when the project's
/// content hash still matches, so the file whose spans these are is byte-for-byte the file the
/// diff is about. Nothing is inferred and no key is invented - the key is the one the graph
/// already holds.
/// </para>
/// <para>
/// This is the whole reason seed resolution costs nothing on a warm run. Reading declarations back
/// out of a compilation meant producing one, which parses every file in the project and resolves
/// every reference - the single largest cost in a run that rebuilds nothing.
/// </para>
/// </remarks>
public sealed record DeclarationSite
{
    public required string ProjectName { get; init; }

    /// <summary>The declaring file, as the compilation saw it: an absolute path.</summary>
    public required string FilePath { get; init; }

    /// <summary>One-based, inclusive, and spanning the declaration only - not its leading
    /// trivia, which would stretch the first member of a file up over the using directives.</summary>
    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public required string Key { get; init; }

    /// <summary>
    /// A type declaration rather than a member. The distinction decides precedence: a change
    /// inside a member belongs to that member, and only a change that falls in no member at all is
    /// read as a change to the type header.
    /// </summary>
    public required bool IsType { get; init; }

    /// <summary>
    /// A constant or an enum member - baked into its callers at compile time, so no caller carries
    /// a reference the graph could follow.
    /// </summary>
    public bool IsInlined { get; init; }

    public bool Intersects(int start, int end) => StartLine <= end && start <= EndLine;
}
