namespace Tia.Core.Diff;

public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
}

/// <summary>One entry of a resolved diff, with the changed line ranges on both sides.</summary>
public sealed record ChangedFile
{
    /// <summary>Repository-relative path on the new side (for deletions, the path that was removed).</summary>
    public required string Path { get; init; }

    /// <summary>Repository-relative path on the old side, when it differs (renames and copies).</summary>
    public string? OldPath { get; init; }

    public required FileChangeKind Kind { get; init; }

    /// <summary>Changed line ranges in the new revision. Empty for deletions.</summary>
    public IReadOnlyList<LineRange> NewLines { get; init; } = [];

    /// <summary>Changed line ranges in the base revision. Empty for additions.</summary>
    public IReadOnlyList<LineRange> OldLines { get; init; } = [];

    /// <summary>
    /// Set when the file is inside a submodule whose gitlink moved, and was found by diffing the
    /// submodule itself (#154). Its old side is in the submodule's history, not this repository's.
    /// </summary>
    public SubmoduleOrigin? Submodule { get; init; }

    public bool IsCSharp => Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public bool ExistsOnNewSide => Kind != FileChangeKind.Deleted;

    /// <summary>The path this file had in the base revision, or null when it did not exist there.</summary>
    public string? OldSidePath => Kind == FileChangeKind.Added ? null : OldPath ?? Path;

    public override string ToString() =>
        OldPath is null ? $"{Kind} {Path}" : $"{Kind} {OldPath} -> {Path}";
}

/// <param name="Path">The submodule's path in the repository, with forward slashes.</param>
/// <param name="OldCommit">The commit its gitlink recorded at the base revision.</param>
public sealed record SubmoduleOrigin(string Path, string OldCommit)
{
    /// <summary><paramref name="repositoryPath"/> as the submodule names it.</summary>
    public string Inner(string repositoryPath) => repositoryPath[(Path.Length + 1)..];
}

/// <summary>The full resolved diff between a base commit and the working tree.</summary>
public sealed record DiffResult
{
    public required string BaseRef { get; init; }

    public required string BaseCommit { get; init; }

    public required IReadOnlyList<ChangedFile> Files { get; init; }
}
