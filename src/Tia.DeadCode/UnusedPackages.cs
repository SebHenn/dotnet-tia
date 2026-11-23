using Microsoft.CodeAnalysis;

namespace Tia.DeadCode;

/// <summary>One project, with every framework copy of its compilation and what its restore produced.</summary>
public sealed record PackageProject(string Name, IReadOnlyList<Compilation> Compilations, PackageAssets Assets);

/// <summary>A <c>PackageReference</c> whose assemblies nothing in the project source uses.</summary>
public sealed record UnusedPackage(string Project, string Package);

/// <summary>
/// Finds <c>PackageReference</c>s a project lists and does not use.
/// </summary>
/// <remarks>
/// <para>
/// What it can see is the source. A package loaded by name at run time (a logging sink named in
/// configuration, a plug-in) is reported too, which is why the answer is "nothing in the source
/// needs it", not "it can be removed".
/// </para>
/// <para>
/// Each of these only drops a report. A package with no assembly for the compiler - an analyzer, a
/// build task, a test adapter, a meta-package - is never judged: nothing the compiler could use
/// ever arrives from it. A package is judged by the assemblies only it brings, so one that
/// another listed package also depends on is judged together with what it shares. It is reported
/// only if Roslyn names none of those assemblies as used in any framework copy of the project and
/// deleting them from the compilation adds no error.
/// </para>
/// </remarks>
public static class UnusedPackages
{
    public static IReadOnlyList<UnusedPackage> Find(IReadOnlyList<PackageProject> projects, CancellationToken cancellationToken = default)
    {
        var found = new List<UnusedPackage>();
        foreach (var project in projects)
        {
            foreach (var package in project.Assets.Direct.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var own = project.Assets.Assemblies.GetValueOrDefault(package);
                if (own is null || own.Count == 0)
                {
                    continue;
                }

                // No framework copy lists an assembly of the package: the paths in the assets file
                // and in the compilation do not line up, and nothing can be said.
                if (!project.Compilations.Any(compilation => References(compilation, own).Count > 0))
                {
                    continue;
                }

                var removable = ExclusiveAssemblies(project.Assets, package);
                if (!project.Compilations.Any(compilation => Needed(compilation, own, removable, cancellationToken)))
                {
                    found.Add(new UnusedPackage(project.Name, package));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The assemblies that leave the compilation with the package: its own and those of every
    /// package reachable only through it.
    /// </summary>
    private static HashSet<string> ExclusiveAssemblies(PackageAssets assets, string package)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in assets.Direct.Where(other => !string.Equals(other, package, StringComparison.Ordinal)))
        {
            Reach(assets, other, kept);
        }

        var leaving = new HashSet<string>(StringComparer.Ordinal);
        Reach(assets, package, leaving);
        leaving.ExceptWith(kept);

        return new HashSet<string>(
            leaving.SelectMany(id => assets.Assemblies.GetValueOrDefault(id) ?? (IReadOnlySet<string>)new HashSet<string>()),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void Reach(PackageAssets assets, string package, HashSet<string> seen)
    {
        if (!seen.Add(package))
        {
            return;
        }

        foreach (var dependency in assets.Dependencies.GetValueOrDefault(package) ?? (IReadOnlySet<string>)new HashSet<string>())
        {
            Reach(assets, dependency, seen);
        }
    }

    private static List<PortableExecutableReference> References(Compilation compilation, IReadOnlySet<string> assemblies) =>
        [.. compilation.References
            .OfType<PortableExecutableReference>()
            .Where(reference => reference.FilePath is { } path && assemblies.Contains(PackageAssets.Normalise(path)))];

    /// <summary>
    /// Whether this framework copy needs the package. Its own assemblies are the ones whose use
    /// counts; the ones that would be removed with it - its own and what only it brings - are the
    /// ones whose absence is tried. They differ when another listed package also depends on this
    /// one: the package may be used and stay in the compilation either way, or be unused and
    /// stay in the compilation, in which case removing the reference changes nothing to compile.
    /// </summary>
    private static bool Needed(Compilation compilation, IReadOnlySet<string> own, HashSet<string> removable, CancellationToken cancellationToken)
    {
        // By path, not by instance: the workspace can hand back a reference that is not the very
        // object the compilation lists, and an identity match would then read "used" as "unused".
        var ownPaths = References(compilation, own).Select(reference => reference.FilePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (compilation.GetUsedAssemblyReferences(cancellationToken)
            .OfType<PortableExecutableReference>()
            .Any(used => used.FilePath is { } usedPath && ownPaths.Contains(usedPath)))
        {
            return true;
        }

        var leaving = References(compilation, removable);
        if (leaving.Count == 0)
        {
            return false;
        }

        var before = ErrorCount(compilation, cancellationToken);
        return ErrorCount(compilation.RemoveReferences(leaving), cancellationToken) > before;
    }

    private static int ErrorCount(Compilation compilation, CancellationToken cancellationToken) =>
        compilation.GetDiagnostics(cancellationToken).Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
