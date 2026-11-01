using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tia.Core.Analysis;
using Tia.Core.Infrastructure;

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
    /// <remarks>
    /// Parallel, like the surface-hash loop beside it in <c>GraphBuilder</c> and for the same
    /// reason: this reads and hashes every document of every project, on every run, including the
    /// ones nothing else will touch. Running it one project after another left it as the only
    /// single-threaded stretch of a phase whose other half was already spread across every core.
    /// </remarks>
    public static Dictionary<string, string> ComputeContentHashes(
        IReadOnlyList<ProjectContext> projects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var hashes = new string[projects.Count];

        Parallel.For(0, projects.Count, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        }, index => hashes[index] = ComputeContent(projects[index], cancellationToken));

        // Built afterwards rather than in the loop: a dictionary written from several threads is a
        // corrupted dictionary, and the ordering here is what makes the result reproducible.
        var content = new Dictionary<string, string>(projects.Count, StringComparer.Ordinal);
        for (var index = 0; index < projects.Count; index++)
        {
            content[projects[index].Name] = hashes[index];
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fail closed. A constant here would be a stable hash, so a project file that could not
            // be read would compare equal to the last time it could not be read - and, worse, the
            // value gets *stored* with the fragment, so one transient IO error pins the cache to a
            // hash that no successful read can ever produce again. A fresh value forces a rebuild
            // now and leaves nothing reusable behind.
            builder.Append("unreadable:").Append(Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture));
        }

        var documents = new List<string>();

        // Compile items are the obvious input. The other two are not optional extras: a source
        // generator reads AdditionalFiles and is configured by .editorconfig, so a fragment that
        // ignored them would be reused unchanged after the generated code underneath it moved.
        foreach (var document in project.Project.Documents
                     .Concat<TextDocument>(project.Project.AdditionalDocuments)
                     .Concat(project.Project.AnalyzerConfigDocuments))
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

        // By path and by identity. A path alone stays the same when a HintPath DLL is replaced, or a
        // project outside the solution is rebuilt, and the fragment built against the old one was
        // reused (#155). A NuGet package moves to a new path when its version does.
        foreach (var reference in project.Project.MetadataReferences
                     .Select(r => (r.Display ?? "?") + "@" + (r is PortableExecutableReference { FilePath: { Length: > 0 } path } ? Identity(path) : "-"))
                     .Order(StringComparer.Ordinal))
        {
            builder.Append(reference).Append('\n');
        }

        // Upgrading a generator package changes what it emits without touching a single file in
        // the project.
        foreach (var analyzer in project.Project.AnalyzerReferences.Select(a => a.FullPath ?? a.Display ?? "?").Order(StringComparer.Ordinal))
        {
            builder.Append(analyzer).Append('\n');
        }

        // The project file's own text is hashed, but not what it imports: a DefineConstants or
        // LangVersion set in Directory.Build.props changes which `#if` code compiles, and with it
        // the edges, without touching the project file. The diff that makes that change runs
        // everything, but a fragment built before it would be reused afterwards (#115). These are
        // the evaluated values, so where they were set does not matter.
        if (project.Project.ParseOptions is { } parse)
        {
            builder.Append("symbols=").AppendJoin(';', parse.PreprocessorSymbolNames.Order(StringComparer.Ordinal)).Append('\n');
            builder.Append("parse=").Append(parse.Kind).Append(';').Append(parse.DocumentationMode)
                .Append(';').Append(parse is CSharpParseOptions csharp ? csharp.LanguageVersion.ToString() : "?").Append('\n');
        }

        if (project.Project.CompilationOptions is { } compilation)
        {
            builder.Append("compilation=").Append(compilation.OutputKind).Append(';').Append(compilation.NullableContextOptions)
                .Append(';').Append(compilation is CSharpCompilationOptions { AllowUnsafe: true } ? "unsafe" : "safe").Append('\n');
        }

        return Hash(builder.ToString());
    }

    /// <remarks>
    /// <para>
    /// The format below is the whole point of the method and is deliberately maximal.
    /// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>, which this used to use, renders a
    /// method as its name and nothing else: <c>Value(int)</c> and <c>Value(long)</c> hash the same,
    /// so changing a parameter type left every dependent's cached fragment looking valid. That is
    /// the worst failure this cache has - not a slow run, a wrong answer - and it is invisible,
    /// because the stale fragment merges into the graph without complaint. Parameters, return
    /// types, accessibility, modifiers and constant values are all in for that reason; a public
    /// const in particular is inlined into its dependents, so its *value* is part of the surface.
    /// </para>
    /// </remarks>
    private static readonly SymbolDisplayFormat SurfaceFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters |
                         SymbolDisplayGenericsOptions.IncludeTypeConstraints |
                         SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters |
                       SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeModifiers |
                       SymbolDisplayMemberOptions.IncludeAccessibility |
                       SymbolDisplayMemberOptions.IncludeExplicitInterface |
                       SymbolDisplayMemberOptions.IncludeConstantValue |
                       SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeName |
                          SymbolDisplayParameterOptions.IncludeDefaultValue |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut |
                          SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                              SymbolDisplayMiscellaneousOptions.ExpandNullable);

    /// <summary>
    /// Everything a referencing project could bind against: type names, what they derive from and
    /// implement, and every member signature that is reachable from outside the assembly. Symbol
    /// enumeration only - no method bodies are bound, which is what keeps this cheap enough to do
    /// for every project on every run.
    /// </summary>
    /// <remarks>
    /// Private members are excluded because no other assembly can name one, so adding, removing or
    /// re-signing a private helper cannot change how a dependent binds. That exclusion is worth
    /// stating precisely, because hashing them was not merely redundant: on NodaTime a private
    /// helper added to NodaTime.Testing invalidated the 30-second test project downstream of it,
    /// and adding a private helper is one of the most ordinary edits there is. Internal members
    /// stay in - <c>InternalsVisibleTo</c> makes them bindable, and NodaTime uses it.
    /// </remarks>
    public static string ComputeSurface(ProjectContext project, CancellationToken cancellationToken = default) =>
        ComputeSurface(project.Compilation, cancellationToken);

    /// <inheritdoc cref="ComputeSurface(ProjectContext, CancellationToken)"/>
    public static string ComputeSurface(Compilation compilation, CancellationToken cancellationToken = default)
    {
        var entries = new List<string>();

        foreach (var type in ReferenceGraphBuilder.EnumerateSourceTypes(compilation, cancellationToken))
        {
            if (!IsVisibleOutsideAssembly(type))
            {
                continue;
            }

            var builder = new StringBuilder()
                .Append(type.DeclaredAccessibility).Append(' ')
                .Append(type.IsAbstract ? "abstract " : string.Empty)
                .Append(type.IsSealed ? "sealed " : string.Empty)
                .Append(type.IsRecord ? "record " : string.Empty)
                .Append(type.TypeKind).Append(' ')
                .Append(type.ToDisplayString(SurfaceFormat))
                .Append(" : ")
                .Append(type.BaseType?.ToDisplayString(SurfaceFormat) ?? "-");

            foreach (var iface in type.AllInterfaces.Select(i => i.ToDisplayString(SurfaceFormat)).Order(StringComparer.Ordinal))
            {
                builder.Append(", ").Append(iface);
            }

            foreach (var member in type.GetMembers()
                         .Where(IsVisibleOutsideAssembly)
                         .Select(m => m.ToDisplayString(SurfaceFormat))
                         .Order(StringComparer.Ordinal))
            {
                builder.Append('\n').Append(member);
            }

            entries.Add(builder.ToString());
        }

        entries.Sort(StringComparer.Ordinal);
        return Hash(string.Join('\n', entries));
    }

    /// <summary>
    /// Whether any code outside this assembly could reach the symbol. Only <c>private</c> is ruled
    /// out - and a private container hides everything inside it, however the nested members are
    /// declared, so the whole containing chain has to be checked rather than the symbol alone.
    /// </summary>
    private static bool IsVisibleOutsideAssembly(ISymbol symbol)
    {
        // Roslyn reports an explicit interface implementation as private, and by the language rules
        // it is: nothing can name it directly. It is still reachable through the interface, and the
        // graph has an edge saying so, so adding or removing one has to move the surface hash.
        if (IsExplicitInterfaceImplementation(symbol))
        {
            return true;
        }

        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility == Accessibility.Private)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExplicitInterfaceImplementation(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0,
        IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
        IEventSymbol @event => @event.ExplicitInterfaceImplementations.Length > 0,
        _ => false,
    };

    private static string Hash(string value) => Hashing.Sha256Hex(value, 32);

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string> Identities = new();

    /// <summary>
    /// What changes when a referenced assembly's content does: its MVID, read from the metadata
    /// without loading the assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not its timestamp, which #115 rejected: a restore rewrites the times of every package file,
    /// so a CI job that cached the graph would rebuild every project after each one. A
    /// deterministic build, the SDK's default, derives the MVID from the content, so the same
    /// assembly built twice keeps it. A non-deterministic build gets a new one each time, which
    /// costs a rebuild of the projects that reference it and never a stale fragment.
    /// </para>
    /// <para>
    /// Remembered by path, length and write time, because the same framework and package assemblies
    /// are referenced by every project. That memo only saves reading a file again; the fingerprint
    /// itself holds the MVID.
    /// </para>
    /// </remarks>
    internal static string Identity(string path)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                // Not built yet. Stable, so a reference that never builds costs nothing; the run
                // that first builds it moves the hash once.
                return "missing";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unreadable();
        }

        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        if (Identities.TryGetValue(key, out var known))
        {
            return known;
        }

        string identity;
        try
        {
            using var stream = file.OpenRead();
            identity = Mvid(stream) ?? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.FullName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being written, as by a build running beside the analysis. Fail closed, as for an
            // unreadable project file, and remember nothing.
            return Unreadable();
        }

        Identities[key] = identity;
        return identity;
    }

    /// <summary>Null when the file is not an assembly, and its content is all there is to go by.</summary>
    private static string? Mvid(Stream stream)
    {
        try
        {
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!reader.HasMetadata)
            {
                return null;
            }

            var metadata = reader.GetMetadataReader();
            return "mvid:" + metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("n", CultureInfo.InvariantCulture);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static string Unreadable() => "unreadable:" + Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);
}
