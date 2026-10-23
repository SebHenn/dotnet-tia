using Microsoft.CodeAnalysis;
using Tia.Core.Diff;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// Maps changed line ranges onto symbols using the declaration positions stored with the graph
/// fragment, instead of a semantic model.
/// </summary>
/// <remarks>
/// <para>
/// The rules are <see cref="ChangedSymbolResolver"/>'s, line for line, because they are the ones
/// that are easy to get wrong and none of them is a refinement: members win over the type that
/// contains them, a change to a type header is a change to every member, constants are inlined
/// into callers, a change outside every declaration rebinds the whole file, and top-level
/// statements and assembly attributes, which no declaration owns, widen their project.
/// </para>
/// <para>
/// What changes is only where the answer comes from. A <see cref="DeclarationSite"/> was recorded
/// from a semantic model, over the same bytes this file still holds - a fragment is reused only
/// when its project's content hash matches - so no key is derived, guessed or reconstructed here.
/// The alternative, deriving a documentation comment id from syntax, is not available: the id
/// embeds resolved parameter types, so a syntactic derivation is an approximation, and an
/// approximate seed is a missed test.
/// </para>
/// </remarks>
public static class DeclarationSiteResolver
{
    /// <param name="sites">Every declaration in one file, from one project's fragment.</param>
    /// <param name="lineCount">
    /// Lines in the file at HEAD. A trailing deletion produces a range that starts past the end,
    /// which names no declaration and must not be read as one that names them all.
    /// </param>
    /// <param name="parse">
    /// The file's syntax tree, produced only if a change falls outside every declaration, even in
    /// part. That is the one case that cannot be decided from positions: a global using, a top-level
    /// statement and an assembly attribute are not declarations, and changing any of them reaches
    /// beyond the file.
    /// </param>
    public static SymbolChangeSet Resolve(
        IReadOnlyList<DeclarationSite> sites,
        ImpactGraph graph,
        IReadOnlyList<LineRange> changedLines,
        string projectName,
        string filePath,
        int lineCount,
        bool isNewFile,
        Func<SyntaxTree?> parse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parse);

        var result = new SymbolChangeSet();
        if (changedLines.Count == 0)
        {
            return result;
        }

        foreach (var range in changedLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (range.Start > lineCount)
            {
                continue;
            }

            var matchedMember = false;
            foreach (var site in sites)
            {
                if (site.IsType || !site.Intersects(range.Start, range.End))
                {
                    continue;
                }

                matchedMember = true;
                MarkMember(graph, site, projectName, isNewFile, result);
            }

            // Not inside a member. Either the type header changed (base list, type parameters,
            // attributes) or a member was removed at this position - both are type-wide.
            var matchedType = false;
            if (!matchedMember)
            {
                foreach (var site in sites)
                {
                    if (site.IsType && site.Intersects(range.Start, range.End))
                    {
                        matchedType = true;
                        result.Add(site.Key);
                    }
                }
            }

            // A range wholly inside declarations is decided without the syntax tree, which is what
            // keeps this path from parsing the ordinary edit.
            var matched = matchedMember || matchedType;
            if (matched && UnownedCode.DeclarationsCover(sites.Select(s => new LineRange(s.StartLine, s.EndLine)), range, lineCount))
            {
                continue;
            }

            // Outside every declaration, at least in part: usings, namespace declarations,
            // top-level statements, assembly attributes.
            var root = parse()?.GetRoot(cancellationToken);
            var widened = false;
            if (root is not null && ChangedSymbolResolver.TouchesGlobalUsing(root, range, cancellationToken))
            {
                result.AddProjectWide(projectName, ProjectWideCause.GlobalUsing,
                    $"{Path.GetFileName(filePath)} line {range}: global using or file-level directive changed");
                widened = true;
            }

            if (root is not null)
            {
                widened |= UnownedCode.WidenIfTouched(root, range, projectName, filePath, result, cancellationToken);
            }

            if (widened || matched)
            {
                continue;
            }

            // A plain using directive or a namespace rename rebinds every type in the file.
            var markedAny = false;
            foreach (var site in sites)
            {
                if (site.IsType)
                {
                    result.Add(site.Key);
                    markedAny = true;
                }
            }

            if (root is null)
            {
                // Unreachable for a C# document, which always has a tree. Without one nothing can
                // be said about these lines, and a file of types is already covered above.
                if (!markedAny)
                {
                    result.AddProjectWide(projectName, ProjectWideCause.Unmapped,
                        $"{Path.GetFileName(filePath)} line {range}: changed outside every declaration, in a file that could not be parsed");
                }

                continue;
            }

            if (UnownedCode.WidenIfHeld(root, range, projectName, filePath, result))
            {
                continue;
            }

            if (!markedAny)
            {
                result.Notes.Add(UnownedCode.NothingToRebind(filePath, range));
            }
        }

        return result;
    }

    private static void MarkMember(
        ImpactGraph graph,
        DeclarationSite site,
        string projectName,
        bool isNewFile,
        SymbolChangeSet result)
    {
        if (!site.IsInlined)
        {
            result.Add(site.Key);
            return;
        }

        // Constants and enum members are baked into their callers at compile time, so no caller
        // carries a reference the graph could follow. The only sound answer is to treat the whole
        // declaring type as changed and to widen to the referencing projects.
        var node = graph.TryGetNode(site.Key);
        if (node?.ContainingTypeKey is { } containingType)
        {
            result.Add(containingType);
        }

        if (!isNewFile)
        {
            result.AddProjectWide(projectName, ProjectWideCause.ConstantInlining,
                $"{node?.DisplayName ?? site.Key} is compile-time inlined into its callers");
        }
    }
}
