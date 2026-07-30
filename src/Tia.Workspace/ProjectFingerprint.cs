using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Tia.Core.Analysis;

namespace Tia.Workspace;

/// <summary>
/// Content hashes that decide whether a cached graph fragment can be reused.
/// </summary>
/// <remarks>
/// <para>
/// A project's fragment is a function of two things: its own source, and the *declarations* of
/// everything it references. Its edges are produced by binding its syntax against those
/// declarations - which symbols exist, what they are called, what they derive from and implement.
/// Nothing in it depends on a dependency's method bodies.
/// </para>
/// <para>
/// Folding whole dependency fingerprints in instead was correct but useless: a core library
/// changes on most commits, so on NodaTime a one-line edit invalidated 18 of 21 projects and the
/// cache saved nothing in exactly the case it exists for. Hashing the declaration surface
/// separately keeps the guarantee - a rename, a new base type, a changed signature all move the
/// surface hash - while letting a body-only change invalidate one project instead of the solution.
/// </para>
/// </remarks>
public static class ProjectFingerprint
{
    /// <summary>
    /// Content hashes for every project, computed without producing a single compilation.
    /// Parsing every document is the expensive half of loading a solution, and a project whose
    /// content is unchanged never needs to be parsed at all.
    /// </summary>
    public static Dictionary<string, string> ComputeContentHashes(
        IReadOnlyList<ProjectContext> projects,
        CancellationToken cancellationToken = default)
    {
        var content = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            content[project.Name] = ComputeContent(project, cancellationToken);
        }

        return content;
    }

    /// <summary>
    /// Combines a project's own content hash with the declaration surfaces of everything it
    /// references. Reuse is decided by comparing this against the value stored with the fragment.
    /// </summary>
    public static string Combine(
        string projectName,
        IReadOnlyDictionary<string, string> content,
        IReadOnlyDictionary<string, string> surface,
        IReadOnlyDictionary<string, IReadOnlyList<string>> references)
    {
        var dependencies = new SortedSet<string>(StringComparer.Ordinal);
        Collect(projectName, references, dependencies);
        dependencies.Remove(projectName);

        var builder = new StringBuilder()
            .Append("content=").Append(content.GetValueOrDefault(projectName, "?")).Append(';');

        foreach (var name in dependencies)
        {
            builder.Append(name).Append("=surface:").Append(surface.GetValueOrDefault(name, "?")).Append(';');
        }

        return Hash(builder.ToString());
    }

    private static void Collect(string name, IReadOnlyDictionary<string, IReadOnlyList<string>> references, SortedSet<string> into)
    {
        if (!into.Add(name))
        {
            return;
        }

        foreach (var reference in references.GetValueOrDefault(name, []))
        {
            Collect(reference, references, into);
        }
    }

    private static string ComputeContent(ProjectContext project, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        try
        {
            builder.Append(Hash(File.ReadAllText(project.Descriptor.FilePath)));
        }
        catch (IOException)
        {
            builder.Append("unreadable");
        }

        var documents = new List<string>();
        foreach (var document in project.Project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = document.GetTextAsync(cancellationToken).GetAwaiter().GetResult();
            documents.Add(document.FilePath + ":" + Convert.ToHexStringLower([.. text.GetContentHash()]));
        }

        documents.Sort(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            builder.Append(document).Append('\n');
        }

        foreach (var reference in project.Project.MetadataReferences.Select(r => r.Display ?? "?").Order(StringComparer.Ordinal))
        {
            builder.Append(reference).Append('\n');
        }

        return Hash(builder.ToString());
    }

    /// <summary>
    /// Everything a referencing project could bind against: type names, what they derive from and
    /// implement, and every member signature. Symbol enumeration only - no method bodies are
    /// bound, which is what keeps this cheap enough to do for every project on every run.
    /// </summary>
    public static string ComputeSurface(ProjectContext project, CancellationToken cancellationToken = default)
    {
        var entries = new List<string>();

        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(project.Compilation, cancellationToken))
        {
            var builder = new StringBuilder()
                .Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(" : ")
                .Append(type.BaseType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "-");

            foreach (var iface in type.AllInterfaces.Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Order(StringComparer.Ordinal))
            {
                builder.Append(", ").Append(iface);
            }

            foreach (var member in type.GetMembers().Select(m => m.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Order(StringComparer.Ordinal))
            {
                builder.Append('\n').Append(member);
            }

            entries.Add(builder.ToString());
        }

        entries.Sort(StringComparer.Ordinal);
        return Hash(string.Join('\n', entries));
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
}
