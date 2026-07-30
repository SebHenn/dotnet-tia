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

        foreach (var reference in project.Project.MetadataReferences.Select(r => r.Display ?? "?").Order(StringComparer.Ordinal))
        {
            builder.Append(reference).Append('\n');
        }

        // Upgrading a generator package changes what it emits without touching a single file in
        // the project.
        foreach (var analyzer in project.Project.AnalyzerReferences.Select(a => a.FullPath ?? a.Display ?? "?").Order(StringComparer.Ordinal))
        {
            builder.Append(analyzer).Append('\n');
        }

        return Hash(builder.ToString());
    }

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

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
}
