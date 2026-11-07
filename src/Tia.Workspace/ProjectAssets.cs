using System.Text.Json;

namespace Tia.Workspace;

/// <summary>What a restore says a project resolved, or why that cannot be relied on.</summary>
internal sealed record AssetsReading(IReadOnlySet<string>? Packages, string? Problem);

/// <summary>What a restore says a project resolved.</summary>
internal static class ProjectAssets
{
    /// <summary>
    /// Every package in the project's restored closure, including those reached through a package or
    /// a project reference, or the reason it cannot be relied on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A problem is an answer of its own: a project whose closure is unknown might hold the package,
    /// so the caller cannot rule it out. The default location only, as in
    /// <see cref="WorkspaceHealth.AssetsFilePath"/>, so a repository relocating <c>obj/</c> gets a full
    /// run instead of a narrower one.
    /// </para>
    /// <para>
    /// A failed restore still writes the file, with the error in <c>logs</c> and a closure that is the
    /// old one, or part of the new one. It reads like any other, so the log is what says not to trust
    /// it. Found on cartographer, where an Avalonia downgrade failed with NU1109 and was narrowed on
    /// the closure the failed restore left behind.
    /// </para>
    /// </remarks>
    public static AssetsReading Read(string projectFilePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(WorkspaceHealth.AssetsFilePath(projectFilePath)));
            var root = document.RootElement;

            if (root.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array)
            {
                var errors = logs.EnumerateArray()
                    .Where(log => log.TryGetProperty("level", out var level) &&
                                  string.Equals(level.GetString(), "Error", StringComparison.OrdinalIgnoreCase))
                    .Select(log => log.TryGetProperty("code", out var code) ? code.GetString() : null)
                    .Select(code => code ?? "an error")
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (errors.Count > 0)
                {
                    return new AssetsReading(null, $"its last restore failed ({string.Join(", ", errors)})");
                }
            }

            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in root.GetProperty("libraries").EnumerateObject())
            {
                // Keyed "Name/Version".
                var separator = library.Name.IndexOf('/', StringComparison.Ordinal);
                packages.Add(separator < 0 ? library.Name : library.Name[..separator]);
            }

            return new AssetsReading(packages, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new AssetsReading(null, "its obj/project.assets.json could not be read");
        }
    }
}
