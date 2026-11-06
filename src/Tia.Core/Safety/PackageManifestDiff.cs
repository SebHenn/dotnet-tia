using System.Xml;
using System.Xml.Linq;
using Tia.Core.Diff;

namespace Tia.Core.Safety;

/// <summary>
/// Reads a change to a package manifest as the package versions it moved, when that is all it did
/// (#171).
/// </summary>
/// <remarks>
/// <para>
/// A manifest is a build input, so changing one forces a full run: it could do anything. Most changes
/// to one do a single thing, though, which is to move a version, and a dependency bot opens a pull
/// request for each of those. Every one of them ran the whole suite here, including a test-only bump.
/// </para>
/// <para>
/// This is the rule that lets a build input narrow, so it reads only what it can be sure of. The two
/// sides have to be the same document once comments, formatting and the versions themselves are set
/// aside. A condition, an import, a property, an added or removed entry, or a
/// <c>GlobalPackageReference</c> each make it null, and null stays a full run. A package whose version
/// comes from a property moves by that property changing, which is a change to something else and so
/// is not understood either.
/// </para>
/// </remarks>
public static class PackageManifestDiff
{
    private const string CentralFile = "Directory.Packages.props";

    private static readonly string[] EntryElements = ["PackageVersion", "PackageReference"];

    private static readonly string[] VersionAttributes = ["Version", "VersionOverride"];

    /// <summary>
    /// True for a file this can read. A <c>.csproj</c> and the central file only: a
    /// <c>packages.lock.json</c> or a <c>Directory.Build.props</c> says something else.
    /// </summary>
    public static bool IsManifest(string path)
    {
        var name = Path.GetFileName(path);

        return name.Equals(CentralFile, StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(name).Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a manifest applies by the projects holding a package, not to one project.</summary>
    public static bool IsCentral(string path) =>
        Path.GetFileName(path).Equals(CentralFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The packages whose version differs, which is empty when only comments or formatting did, or
    /// null when anything else changed.
    /// </summary>
    public static IReadOnlySet<string>? ChangedPackages(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (Parse(before) is not { } oldSide || Parse(after) is not { } newSide)
        {
            return null;
        }

        var oldVersions = TakeVersions(oldSide);
        var newVersions = TakeVersions(newSide);

        // With the versions taken out, same document means same entries in the same order.
        if (!XNode.DeepEquals(oldSide, newSide) || oldVersions.Count != newVersions.Count)
        {
            return null;
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < oldVersions.Count; index++)
        {
            if (string.Equals(oldVersions[index].Versions, newVersions[index].Versions, StringComparison.Ordinal))
            {
                continue;
            }

            if (oldVersions[index].Package is not { Length: > 0 } package)
            {
                return null;
            }

            changed.Add(package);
        }

        return changed;
    }

    /// <summary>
    /// The manifests in a diff that moved nothing but package versions, each with the packages it
    /// moved. A file absent from this stays a full run.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Read(IGitClient git, DiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(diff);

        var understood = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var file in diff.Files)
        {
            // An added, deleted or renamed manifest changes which projects exist, or where.
            if (file.Kind != FileChangeKind.Modified || file.OldPath is not null || file.Submodule is not null || !IsManifest(file.Path))
            {
                continue;
            }

            if (git.ShowFile(diff.BaseCommit, file.Path) is not { } before ||
                ReadNewSide(Path.Combine(git.RepositoryRoot, file.Path)) is not { } after)
            {
                continue;
            }

            if (ChangedPackages(before, after) is { } packages)
            {
                understood[file.Path] = packages;
            }
        }

        return understood;
    }

    private static string? ReadNewSide(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static XDocument? Parse(string content)
    {
        try
        {
            var document = XDocument.Parse(content.TrimStart('﻿'));
            document.DescendantNodes().OfType<XComment>().Remove();
            return document;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Blanks each entry's version, in place, and returns what they were in document order.</summary>
    private static List<(string? Package, string Versions)> TakeVersions(XDocument document)
    {
        var taken = new List<(string?, string)>();

        foreach (var entry in document.Descendants().Where(e => EntryElements.Contains(e.Name.LocalName, StringComparer.Ordinal)))
        {
            var versions = new List<string>();

            foreach (var attribute in entry.Attributes().Where(a => VersionAttributes.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            {
                versions.Add($"{attribute.Name.LocalName}={attribute.Value}");
                attribute.Value = string.Empty;
            }

            var package = (string?)entry.Attribute("Include") ?? (string?)entry.Attribute("Update");
            taken.Add((package, string.Join(';', versions)));
        }

        return taken;
    }
}
