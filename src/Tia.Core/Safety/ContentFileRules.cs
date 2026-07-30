namespace Tia.Core.Safety;

/// <summary>
/// The second tier: changes that widen scope rather than bail out. Non-source content is
/// routinely test data, and nothing in a symbol graph connects a <c>.json</c> fixture to the test
/// that loads it by path.
/// </summary>
public static class ContentFileRules
{
    private static readonly HashSet<string> ContentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json",
        ".resx",
        ".sql",
        ".txt",
        ".xml",
        ".csv",
        ".yaml",
        ".yml",
        ".html",
        ".razor",
        ".cshtml",
        ".md",
        ".snap",
        ".approved",
        ".verified",
        ".bin",
        ".dat",
        ".xlsx",
        ".png",
        ".jpg",
    };

    /// <summary>True when a non-source file should widen its owning project to full scope.</summary>
    public static bool IsWideningContent(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 && ContentExtensions.Contains(extension);
    }
}
