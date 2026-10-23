using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Diff;

namespace Tia.Core.Analysis;

/// <summary>
/// Code that the compiler binds but that no declaration owns: top-level statements, and assembly-
/// or module-level attributes.
/// </summary>
/// <remarks>
/// <para>
/// Every other changed line belongs to something with a key. A member owns its body, a type owns
/// its header, and a using directive is read as a change to every type in its file. These two do
/// not fit. Top-level statements compile into an entry point that no source declaration names, so
/// <c>WebApplicationFactory&lt;Program&gt;</c> reaches them without any edge the graph could follow.
/// An assembly attribute such as <c>[assembly: InternalsVisibleTo]</c> or xunit's
/// <c>[assembly: CollectionBehavior]</c> applies to the whole assembly. A change to either used to
/// be recorded as unmapped and then select nothing, while the report printed a widening that
/// widened nothing (#35).
/// </para>
/// <para>
/// The answer is the owning project, plus everything that depends on it. That is a superset, which
/// is the right side to err on. It is also narrow in practice, because it fires only on the lines
/// that hold such code, or on a file that holds nothing else. A using directive, a namespace line or
/// a comment beside the types of an ordinary file still marks those types and nothing more.
/// </para>
/// </remarks>
internal static class UnownedCode
{
    /// <summary>
    /// Widens the project when the range touches a top-level statement or an assembly or module
    /// attribute, and says whether it did.
    /// </summary>
    /// <remarks>
    /// The detail names the file and not the lines, on purpose. The old side reports the same
    /// change from the base revision, and identical details collapse into one widening, so a
    /// modified <c>Program.cs</c> reads as one entry rather than one per side and per hunk.
    /// </remarks>
    public static bool WidenIfTouched(
        SyntaxNode root,
        LineRange range,
        string projectName,
        string filePath,
        SymbolChangeSet result,
        CancellationToken cancellationToken)
    {
        if (root is not CompilationUnitSyntax unit)
        {
            return false;
        }

        var widened = false;
        var tree = root.SyntaxTree;

        foreach (var member in unit.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (member is GlobalStatementSyntax && ChangedSymbolResolver.Span(tree, member).Intersects(range))
            {
                result.AddProjectWide(projectName, ProjectWideCause.Unmapped,
                    $"{Path.GetFileName(filePath)}: a top-level statement changed; it compiles into an entry point " +
                    "that no declaration names, so there is no symbol to seed");
                widened = true;
                break;
            }
        }

        foreach (var attributes in unit.AttributeLists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ChangedSymbolResolver.Span(tree, attributes).Intersects(range))
            {
                result.AddProjectWide(projectName, ProjectWideCause.Unmapped,
                    $"{Path.GetFileName(filePath)}: an assembly- or module-level attribute changed; it applies to the " +
                    "whole assembly rather than to any declaration");
                widened = true;
                break;
            }
        }

        return widened;
    }

    /// <summary>
    /// Widens the project when a change outside every declaration falls in a file that holds
    /// top-level statements or assembly attributes, and says whether it did.
    /// </summary>
    /// <remarks>
    /// This is the rule that already marks every type in a file when one of its using directives
    /// changes, carried over to what those types are not. A using rebinds the statements and the
    /// attribute names in its file just as it rebinds the types, and a deleted statement leaves
    /// only the lines around it in the new revision. Neither has a key, so the project is the
    /// answer. A file that holds neither is left alone.
    /// </remarks>
    public static bool WidenIfHeld(
        SyntaxNode root,
        LineRange range,
        string projectName,
        string filePath,
        SymbolChangeSet result)
    {
        if (root is not CompilationUnitSyntax unit ||
            (unit.AttributeLists.Count == 0 && !unit.Members.Any(m => m is GlobalStatementSyntax)))
        {
            return false;
        }

        result.AddProjectWide(projectName, ProjectWideCause.Unmapped,
            $"{Path.GetFileName(filePath)} line {range}: changed outside every declaration, in a file whose " +
            "top-level statements or assembly attributes it can rebind");
        return true;
    }

    /// <summary>
    /// The note for a change outside every declaration of a file that holds no type, no top-level
    /// statement and no assembly attribute: only usings, a namespace, comments.
    /// </summary>
    /// <remarks>
    /// A note and not a widening, because there is nothing for it to have changed. A deletion that
    /// left such a file behind is read from the base revision, where the removed type, statement
    /// or attribute still is. This used to be reported as an <c>Unmapped</c> widening that widened
    /// nothing, which is the report saying one thing and the selection doing another.
    /// </remarks>
    public static string NothingToRebind(string filePath, LineRange range) =>
        $"{Path.GetFileName(filePath)} line {range}: changed outside every declaration, in a file that holds " +
        "nothing such a change can rebind, so it seeds nothing";

    /// <summary>
    /// Whether every line of the range that exists in the file lies inside one of the declarations.
    /// </summary>
    /// <remarks>
    /// Decides whether the file has to be parsed at all. A range that matched a declaration used to
    /// stop there, which was right while nothing outside a declaration could matter beyond its file.
    /// Now a hunk that runs from the last statement of a <c>Program.cs</c> into the
    /// <c>partial class Program</c> beneath it has to reach the statement too. Only a range that
    /// pokes out of its declarations pays for the parse.
    /// </remarks>
    public static bool DeclarationsCover(IEnumerable<LineRange> declarations, LineRange range, int lineCount)
    {
        var next = range.Start;
        var end = Math.Min(range.End, lineCount);

        foreach (var declaration in declarations.OrderBy(d => d.Start))
        {
            if (next > end)
            {
                break;
            }

            if (declaration.Start > next)
            {
                break;
            }

            if (declaration.End >= next)
            {
                next = declaration.End + 1;
            }
        }

        return next > end;
    }
}
