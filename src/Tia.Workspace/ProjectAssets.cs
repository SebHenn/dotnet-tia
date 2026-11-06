using System.Text.Json;

namespace Tia.Workspace;

/// <summary>What a restore says a project resolved.</summary>
internal static class ProjectAssets
{
    /// <summary>
    /// Every package in the project's restored closure, including those reached through a package or
    /// a project reference, or null when the assets file cannot be read.
    /// </summary>
    /// <remarks>
    /// Null is an answer of its own: an uninspectable project might hold the package, so the caller
    /// cannot rule it out. The default location only, as in <see cref="WorkspaceHealth.AssetsFilePath"/>,
    /// so a repository relocating <c>obj/</c> gets a full run instead of a narrower one.
    /// </remarks>
    public static IReadOnlySet<string>? ResolvedPackages(string projectFilePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(WorkspaceHealth.AssetsFilePath(projectFilePath)));

            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
            {
                // Keyed "Name/Version".
                var separator = library.Name.IndexOf('/', StringComparison.Ordinal);
                packages.Add(separator < 0 ? library.Name : library.Name[..separator]);
            }

            return packages;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
