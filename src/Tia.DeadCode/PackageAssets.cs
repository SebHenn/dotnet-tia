using System.Text.Json;

namespace Tia.DeadCode;

/// <summary>
/// What <c>obj/project.assets.json</c> says about the NuGet packages one project restored: which
/// ones it lists itself, which it pulled in, and the assemblies each contributes to the compiler.
/// </summary>
public sealed class PackageAssets
{
    private PackageAssets(
        IReadOnlySet<string> direct,
        IReadOnlyDictionary<string, IReadOnlySet<string>> dependencies,
        IReadOnlyDictionary<string, IReadOnlySet<string>> assemblies)
    {
        Direct = direct;
        Dependencies = dependencies;
        Assemblies = assemblies;
    }

    /// <summary>Packages the project file lists, by lower-case id.</summary>
    public IReadOnlySet<string> Direct { get; }

    /// <summary>For each package, the packages it depends on, in any target framework.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Dependencies { get; }

    /// <summary>For each package, the full, normalised paths of the assemblies it hands the compiler, in any target framework.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Assemblies { get; }

    public static PackageAssets Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var folders = root.TryGetProperty("packageFolders", out var folderElement)
            ? folderElement.EnumerateObject().Select(folder => folder.Name).ToList()
            : [];

        // "Foo/1.0.0" -> its folder under a package folder, e.g. "foo/1.0.0".
        var packagePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.TryGetProperty("type", out var type) &&
                    type.GetString() == "package" &&
                    library.Value.TryGetProperty("path", out var path) &&
                    path.GetString() is { } relative)
                {
                    packagePath[library.Name] = relative;
                    idOf[library.Name] = IdOf(library.Name);
                }
            }
        }

        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var assemblies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("targets", out var targets))
        {
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var entry in target.Value.EnumerateObject())
                {
                    if (!idOf.TryGetValue(entry.Name, out var id))
                    {
                        continue;
                    }

                    var dependsOn = dependencies.TryGetValue(id, out var existingDependencies)
                        ? existingDependencies
                        : dependencies[id] = new HashSet<string>(StringComparer.Ordinal);
                    if (entry.Value.TryGetProperty("dependencies", out var declared))
                    {
                        foreach (var dependency in declared.EnumerateObject())
                        {
                            dependsOn.Add(dependency.Name.ToLowerInvariant());
                        }
                    }

                    var contributes = assemblies.TryGetValue(id, out var existingAssemblies)
                        ? existingAssemblies
                        : assemblies[id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (entry.Value.TryGetProperty("compile", out var compile))
                    {
                        foreach (var file in compile.EnumerateObject())
                        {
                            if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            foreach (var folder in folders)
                            {
                                contributes.Add(Normalise(Path.Combine(folder, packagePath[entry.Name], file.Name)));
                            }
                        }
                    }
                }
            }
        }

        var direct = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("projectFileDependencyGroups", out var groups))
        {
            foreach (var group in groups.EnumerateObject())
            {
                foreach (var line in group.Value.EnumerateArray())
                {
                    var name = (line.GetString() ?? string.Empty).Split(' ')[0].ToLowerInvariant();
                    if (dependencies.ContainsKey(name))
                    {
                        direct.Add(name);
                    }
                }
            }
        }

        return new PackageAssets(
            direct,
            dependencies.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal),
            assemblies.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal));
    }

    /// <summary>A path as the compiler reports it and the assets file spells it, so the two compare equal.</summary>
    public static string Normalise(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string IdOf(string library) => library[..library.LastIndexOf('/')].ToLowerInvariant();
}
