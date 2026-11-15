using Tia.Core.Model;

namespace Tia.Core.Safety;

/// <summary>
/// Reads a <a href="https://github.com/VerifyTests/Verify">Verify</a> snapshot's file name as the
/// test that owns it, so that changing a snapshot selects that test and not every test in the
/// project that holds it (#172).
/// </summary>
/// <remarks>
/// <para>
/// Verify names a snapshot <c>{TestClassName}.{TestMethodName}_{Parameters}_{UniqueFor}.verified.{ext}</c>
/// (docs/naming.md in that repository). The name is only the owner when nothing overrides it:
/// <c>UseFileName</c>, <c>UseDirectory</c>, <c>UseTypeName</c>, <c>UseMethodName</c> and
/// <c>DerivePathInfo</c> each let a test, or one setting for the whole assembly, put the file
/// anywhere or call it anything. The caller therefore narrows only when none of
/// <see cref="NamingOverrides"/> appears anywhere in the solution, and only when no source file
/// spells the snapshot's own name, which would be a test reading it by hand.
/// </para>
/// <para>
/// Why this cannot cause a miss: with no override, the one thing that reads the file is the Verify
/// call in the test its name denotes. The parameters and unique-for parts are not parsed. Every
/// method whose name the remainder starts with, at a <c>_</c>, <c>.</c> or <c>#</c>, is taken, so a
/// method called <c>A</c> and one called <c>A_b</c> are both taken for <c>C.A_b.verified.txt</c>,
/// which is a superset of the one that is meant. A name that matches no method of a type of that
/// name - a test inherited from a base class, a renamed or deleted test, a generic or nested type
/// Verify spells differently - returns null, and the caller widens the project as it did before.
/// </para>
/// </remarks>
public static class VerifiedSnapshots
{
    /// <summary>Anything that names, places or reads a snapshot other than by convention. Text
    /// matched, not bound: a false match only widens.</summary>
    public static readonly IReadOnlyList<string> NamingOverrides =
    [
        "UseFileName",
        "UseDirectory",
        "UseTypeName",
        "UseMethodName",
        "DerivePathInfo",
        "VerifyFile",
        "VerifyDirectory",
        "VerifyZip",
    ];

    private const string Marker = ".verified.";

    private static readonly char[] Boundaries = ['_', '.', '#'];

    /// <summary>
    /// Splits <c>Class.Method_args.verified.txt</c> into the class name and everything between it
    /// and <c>.verified.</c>. False for anything else, including a <c>.received.</c> file, which is
    /// what a failing run leaves behind and not a snapshot a test was written against.
    /// </summary>
    public static bool TryParse(string path, out string typeName, out string remainder)
    {
        typeName = remainder = string.Empty;

        var name = Path.GetFileName(path.Replace('\\', '/'));
        var marker = name.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
        {
            return false;
        }

        var stem = name[..marker];
        var dot = stem.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == stem.Length - 1)
        {
            return false;
        }

        var type = stem[..dot];
        if (!type.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return false;
        }

        typeName = type;
        remainder = stem[(dot + 1)..];
        return true;
    }

    /// <summary>
    /// The method keys the snapshot's name can denote, or null when it denotes none the graph can
    /// find and the file has to be treated as content.
    /// </summary>
    public static IReadOnlyList<string>? TestsNamedBy(string path, ImpactGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (!TryParse(path, out var typeName, out var remainder))
        {
            return null;
        }

        var found = new List<string>();

        foreach (var node in graph.Nodes.Values)
        {
            if (node.Kind != SymbolNodeKind.Type ||
                SymbolKeys.TypePathOf(node.Key) is not { } typePath ||
                !typePath.Split('.')[^1].Equals(typeName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var memberKey in graph.MembersOfType(node.Key))
            {
                if (graph.TryGetNode(memberKey) is { Kind: SymbolNodeKind.Method } &&
                    SymbolKeys.SimpleNameOf(memberKey) is { Length: > 0 } method &&
                    Denotes(remainder, method))
                {
                    found.Add(memberKey);
                }
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found.Count == 0 ? null : found;
    }

    private static bool Denotes(string remainder, string method) =>
        remainder.StartsWith(method, StringComparison.Ordinal) &&
        (remainder.Length == method.Length || Array.IndexOf(Boundaries, remainder[method.Length]) >= 0);
}
