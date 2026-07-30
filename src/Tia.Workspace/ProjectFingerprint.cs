using System.Security.Cryptography;
using System.Text;

namespace Tia.Workspace;

/// <summary>
/// Content hashes that decide whether a cached graph fragment can be reused.
/// </summary>
/// <remarks>
/// A project's fingerprint folds in its dependencies' fingerprints, because its edges point at
/// symbol keys owned by those dependencies: reusing a fragment whose dependency changed would
/// leave stale edges in the graph, which is the one failure mode a cache must not have.
/// </remarks>
public static class ProjectFingerprint
{
    public static Dictionary<string, string> ComputeAll(IReadOnlyList<ProjectContext> projects)
    {
        var own = new Dictionary<string, string>(StringComparer.Ordinal);
        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            own[project.Name] = ComputeOwn(project);
            references[project.Name] = project.Descriptor.ProjectReferences;
        }

        var combined = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var closure = new SortedSet<string>(StringComparer.Ordinal);
            Collect(project.Name, references, closure);

            var builder = new StringBuilder();
            foreach (var name in closure)
            {
                builder.Append(name).Append('=').Append(own.GetValueOrDefault(name, "?")).Append(';');
            }

            combined[project.Name] = Hash(builder.ToString());
        }

        return combined;
    }

    private static void Collect(string name, Dictionary<string, IReadOnlyList<string>> references, SortedSet<string> into)
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

    private static string ComputeOwn(ProjectContext project)
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
        foreach (var tree in project.Compilation.SyntaxTrees)
        {
            var contentHash = Convert.ToHexStringLower([.. tree.GetText().GetContentHash()]);
            documents.Add(tree.FilePath + ":" + contentHash);
        }

        documents.Sort(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            builder.Append(document).Append('\n');
        }

        foreach (var reference in project.Compilation.ReferencedAssemblyNames.Select(n => n.GetDisplayName()).Order(StringComparer.Ordinal))
        {
            builder.Append(reference).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
}
