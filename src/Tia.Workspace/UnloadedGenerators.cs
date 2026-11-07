using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tia.Workspace;

/// <summary>A source generator a project uses that the workspace could not load.</summary>
/// <param name="Assembly">The generator assembly's file name.</param>
/// <param name="Reason">Why it did not load, as a clause.</param>
internal sealed record UnloadedGenerator(string Assembly, string Reason);

/// <summary>
/// Finds the source generators a project uses that the workspace silently skipped.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn refuses an analyzer assembly built against a newer compiler than the one loading it, and
/// says so only through an event nobody is listening to. Its generators then produce nothing, so
/// their output is missing from the compilation and from the graph, and a change that only that
/// output reads selects nothing. The Razor generator in SDK 10.0.403 is built against Roslyn 5.9,
/// and a tool carrying 5.6 lost every Razor view and page that way, with no word of it.
/// </para>
/// <para>
/// A failed load cannot be asked what it would have produced, so this compares what an assembly
/// declares with what was loaded: the types carrying <c>[Generator]</c>, read from its metadata
/// without loading it, against the C# generators <see cref="AnalyzerReference.GetGenerators(string)"/> loaded.
/// </para>
/// </remarks>
internal static class UnloadedGenerators
{
    private static readonly Version Carried = typeof(Compilation).Assembly.GetName().Version ?? new Version();

    private static readonly ConcurrentDictionary<(string Path, DateTime Written), Declared> DeclaredByAssembly = new();

    private sealed record Declared(int Generators, Version? Compiler);

    public static IReadOnlyList<UnloadedGenerator> In(Project project)
    {
        var unloaded = new List<UnloadedGenerator>();

        foreach (var reference in project.AnalyzerReferences.OfType<AnalyzerFileReference>())
        {
            if (Inspect(reference) is { } found && !EmitsNothingHere(found, project))
            {
                unloaded.Add(found);
            }
        }

        return unloaded;
    }

    /// <summary>The generator <paramref name="reference"/> declares and did not load, if any.</summary>
    public static UnloadedGenerator? Inspect(AnalyzerFileReference reference)
    {
        var declared = ReadDeclared(reference.FullPath);
        if (declared.Generators == 0)
        {
            return null;
        }

        int loaded;
        try
        {
            // Not GetGeneratorsForAllLanguages: for the SDK's own interop generators it returns 1 where
            // the C# list has all 3, which would report a loaded generator as missing.
            loaded = reference.GetGenerators(LanguageNames.CSharp).Length;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException or TypeLoadException)
        {
            loaded = 0;
        }

        if (loaded >= declared.Generators)
        {
            return null;
        }

        var reason = declared.Compiler is { } compiler && compiler > Carried
            ? $"it is built against Roslyn {Short(compiler)} and this tia carries {Short(Carried)}"
            : $"{declared.Generators - loaded} of its {declared.Generators} generator(s) could not be loaded";

        return new UnloadedGenerator(Path.GetFileName(reference.FullPath), reason);
    }

    /// <summary>
    /// The Razor generator emits nothing for a project with no Razor files, and every
    /// <c>Microsoft.NET.Sdk.Web</c> project references it, so a minimal API is not widened for it.
    /// </summary>
    private static bool EmitsNothingHere(UnloadedGenerator generator, Project project) =>
        generator.Assembly.StartsWith("Microsoft.CodeAnalysis.Razor.", StringComparison.OrdinalIgnoreCase) &&
        !project.AdditionalDocuments.Any(d =>
            d.FilePath is { } path &&
            (path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)));

    private static Declared ReadDeclared(string path)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Declared(0, null);
        }

        return DeclaredByAssembly.GetOrAdd((path, written), static key => Read(key.Path));
    }

    private static Declared Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return new Declared(0, null);
            }

            var metadata = pe.GetMetadataReader();

            Version? compiler = null;
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                if (metadata.StringComparer.Equals(reference.Name, "Microsoft.CodeAnalysis"))
                {
                    compiler = reference.Version;
                }
            }

            var generators = 0;
            foreach (var handle in metadata.TypeDefinitions)
            {
                foreach (var attribute in metadata.GetTypeDefinition(handle).GetCustomAttributes())
                {
                    if (IsGeneratorAttribute(metadata, metadata.GetCustomAttribute(attribute)))
                    {
                        generators++;
                        break;
                    }
                }
            }

            return new Declared(generators, compiler);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return new Declared(0, null);
        }
    }

    private static bool IsGeneratorAttribute(MetadataReader metadata, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference)
        {
            return false;
        }

        var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        if (parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
        return metadata.StringComparer.Equals(type.Name, "GeneratorAttribute") &&
               metadata.StringComparer.Equals(type.Namespace, "Microsoft.CodeAnalysis");
    }

    private static string Short(Version version) => $"{version.Major}.{version.Minor}";
}
