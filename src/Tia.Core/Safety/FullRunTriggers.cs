using Tia.Core.Diff;

namespace Tia.Core.Safety;

/// <summary>
/// The first tier of the safety model: changes whose blast radius cannot be bounded by symbol
/// analysis at all. A tool that skips a test which would have failed is worse than no tool, so
/// these bail out to a full run and say why rather than guessing.
/// </summary>
public static class FullRunTriggers
{
    private static readonly string[] ExactNames =
    [
        "global.json",
        "nuget.config",
        "packages.lock.json",
        ".editorconfig",
        "dotnet-tools.json",
    ];

    private static readonly string[] Prefixes =
    [
        "directory.build.",
        "directory.packages.",
        "directory.solution.",
    ];

    private static readonly string[] Extensions =
    [
        ".csproj",
        ".vbproj",
        ".fsproj",
        ".sln",
        ".slnx",
        ".slnf",
        ".props",
        ".targets",
    ];

    /// <summary>Returns the reason this path forces a full run, or null when it does not.</summary>
    public static string? Match(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Length == 0)
        {
            return null;
        }

        var lower = fileName.ToLowerInvariant();

        foreach (var name in ExactNames)
        {
            if (lower == name)
            {
                return $"{path} changed - it can alter how every project builds";
            }
        }

        foreach (var prefix in Prefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
            {
                return $"{path} changed - it applies to every project beneath it";
            }
        }

        var extension = Path.GetExtension(lower);
        foreach (var candidate in Extensions)
        {
            if (extension == candidate)
            {
                return $"{path} changed - build inputs are outside the symbol graph";
            }
        }

        return null;
    }

    /// <summary>Every full-run reason present in a diff.</summary>
    public static IReadOnlyList<string> Scan(IEnumerable<ChangedFile> files)
    {
        var reasons = new List<string>();
        foreach (var file in files)
        {
            var reason = Match(file.Path);
            if (reason is not null)
            {
                reasons.Add(reason);
            }
            else if (file.OldPath is not null && Match(file.OldPath) is { } oldReason)
            {
                reasons.Add(oldReason);
            }
        }

        return reasons;
    }
}
