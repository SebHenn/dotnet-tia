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

    /// <param name="Unreadable">
    /// The assembly could not be opened, so how many generators it declares is not known. Not the
    /// same as none: zero is a statement about the assembly, and this is the absence of one.
    /// </param>
    private sealed record Declared(int Generators, Version? Compiler, bool Unreadable = false);

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

        // Fail closed. An assembly Roslyn could not open loads no generator either, so counting
        // it as declaring none made declared and loaded agree at zero - the one unloaded generator
        // that was never reported. Reporting an analyzer that holds no generator is a superset: the
        // project is widened for output that was never going to exist, and says why.
        if (declared.Unreadable)
        {
            return new UnloadedGenerator(Path.GetFileName(reference.FullPath), "it could not be read, so what it generates is not known");
        }

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
        try
        {
            // Read throws rather than answering when the file cannot be opened, so the failure is
            // never stored: a file a build was still writing keeps its timestamp once it lands,
            // and a remembered failure would then outlive the reason for it in a resident process.
            var written = File.GetLastWriteTimeUtc(path);
            return DeclaredByAssembly.GetOrAdd((path, written), static key => Read(key.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Declared(0, null, Unreadable: true);
        }
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
        catch (BadImageFormatException)
        {
            // Opened, and not a .NET assembly. That is an answer: it declares no generator.
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
