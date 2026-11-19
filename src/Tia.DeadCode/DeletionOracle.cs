using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tia.DeadCode;

/// <summary>One compilation of the solution, as the oracle needs to see it.</summary>
/// <param name="ReferencedAssemblies">Assembly names of the projects this one references.</param>
public sealed record OracleProject(string Name, Compilation Compilation, IReadOnlyList<string> ReferencedAssemblies);

public enum DeletionOutcome
{
    /// <summary>Removing the declaration left every compilation exactly as clean as before.</summary>
    Safe,

    /// <summary>Removing it broke a compilation: a definite false positive.</summary>
    Broken,

    /// <summary>
    /// Removing it breaks only a build that promotes warnings to errors - a doc comment's
    /// <c>cref</c> to it, say. Nothing uses the declaration; something still mentions it.
    /// </summary>
    Warned,

    /// <summary>The declaration could not be found in a syntax tree, so nothing was tested.</summary>
    Unlocated,
}

/// <param name="Errors">The new errors, at most <see cref="DeletionOracle.MaxErrors"/> of them.</param>
public sealed record DeletionVerdict(Finding Finding, DeletionOutcome Outcome, IReadOnlyList<string> Errors);

/// <summary>
/// Checks a finding by deleting it: if the solution still compiles, the declaration was not used.
/// </summary>
/// <remarks>
/// <para>
/// Only a compile error proves a false positive. A deletion that compiles can still break a program
/// that reaches the declaration through reflection or a serializer, so <see cref="DeletionOutcome.Safe"/>
/// is "not disproved" and the measured precision is an upper bound. That is the honest direction for
/// a number that gates a release, and the test run is the next rung, not this one.
/// </para>
/// <para>
/// Done on immutable compilations, not on disk: a deletion is a replaced syntax tree, and a project
/// that references the edited one is recompiled against the edited compilation. Nothing is written.
/// </para>
/// </remarks>
public static class DeletionOracle
{
    public const int MaxErrors = 5;

    /// <param name="companions">
    /// Findings that are not checked themselves but are deleted along with a break, because a cluster
    /// of dead code does not stop at the findings that were sampled.
    /// </param>
    public static IReadOnlyList<DeletionVerdict> Check(
        string root,
        IReadOnlyList<OracleProject> projects,
        IEnumerable<Finding> findings,
        IReadOnlyList<Finding>? companions = null,
        CancellationToken cancellationToken = default)
    {
        var baselines = new Dictionary<OracleProject, Dictionary<string, int>>();
        var verdicts = new List<DeletionVerdict>();
        Dictionary<Finding, HashSet<string>>? names = null;
        var all = findings as IReadOnlyList<Finding> ?? [.. findings];
        IReadOnlyList<Finding> universe = companions is null ? all : [.. all.Union(companions)];

        foreach (var finding in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alone = CheckOne(root, projects, [finding], baselines, cancellationToken);

            // An interface member and the members that implement it, or a member and the only
            // method that used it, are dead together and cannot be deleted one at a time: each
            // deletion alone leaves the other pointing at nothing. That is a property of the
            // oracle's one-at-a-time probe, not of the finding, so a break is retried with every
            // other finding deleted as well, in any file - which is what removing the cluster
            // would do. A genuine false positive still breaks, because something live uses it.
            if (alone.Outcome == DeletionOutcome.Broken && universe.Count > 1)
            {
                names ??= IdentifiersByFinding(root, projects, universe, cancellationToken);
                var together = Cluster(finding, universe, names);
                if (together.Count > 1)
                {
                    var group = CheckOne(root, projects, together, baselines, cancellationToken);
                    if (group.Outcome != DeletionOutcome.Broken)
                    {
                        alone = alone with { Outcome = group.Outcome, Errors = [] };
                    }
                }
            }

            verdicts.Add(alone);
        }

        return verdicts;
    }

    private static DeletionVerdict CheckOne(
        string root,
        IReadOnlyList<OracleProject> projects,
        IReadOnlyList<Finding> group,
        Dictionary<OracleProject, Dictionary<string, int>> baselines,
        CancellationToken cancellationToken)
    {
        var finding = group[0];
        var errors = new List<string>();
        var warnings = new List<string>();
        var located = false;

        // A multi-targeted project is loaded once per framework, and the file is in each.
        foreach (var project in projects)
        {
            var tree = TreeOf(project, root, finding.File);
            if (tree is null)
            {
                continue;
            }

            // The finding itself must be there; the companions are removed where they are found.
            if (Declaration(tree, finding, cancellationToken) is null)
            {
                continue;
            }

            located = true;
            var without = project.Compilation;
            foreach (var file in group.Select(member => member.File).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TreeOf(project, root, file) is not { } fileTree)
                {
                    continue;
                }

                var declarations = group
                    .Where(member => string.Equals(member.File, file, StringComparison.OrdinalIgnoreCase))
                    .Select(member => Declaration(fileTree, member, cancellationToken))
                    .OfType<SyntaxNode>()
                    .ToList();
                if (declarations.Count > 0)
                {
                    without = without.ReplaceSyntaxTree(fileTree, RemoveDeclarations(fileTree, declarations));
                }
            }

            // A deletion that breaks the declaring project is already a false positive; the
            // dependents are only worth compiling when it did not, because a dependent of a
            // project that no longer builds reports errors that are the same one error.
            AddNew(project, without, errors, warnings, baselines, cancellationToken);
            if (errors.Count > 0)
            {
                continue;
            }

            var assembly = project.Compilation.AssemblyName ?? string.Empty;
            foreach (var dependent in projects.Where(other => other.ReferencedAssemblies.Contains(assembly, StringComparer.Ordinal)))
            {
                if (Reference(dependent.Compilation, project.Compilation, assembly) is not { } existing)
                {
                    // Either it references another framework's copy of this project, which that copy's
                    // own pass covers, or the reference is not this compilation at all. The first is
                    // the usual case; `unchecked` makes the second visible rather than a silent Safe.
                    if (!dependent.Compilation.References.OfType<CompilationReference>().Any(r => string.Equals(r.Compilation.AssemblyName, assembly, StringComparison.Ordinal)))
                    {
                        errors.Add($"{dependent.Name}: could not find its reference to {assembly}, so the deletion was not checked against it");
                    }

                    continue;
                }

                var swapped = dependent.Compilation.ReplaceReference(existing, without.ToMetadataReference());
                AddNew(dependent, swapped, errors, warnings, baselines, cancellationToken);
            }
        }

        if (errors.Count > 0)
        {
            return new DeletionVerdict(finding, DeletionOutcome.Broken, [.. errors.Take(MaxErrors)]);
        }

        if (warnings.Count > 0)
        {
            return new DeletionVerdict(finding, DeletionOutcome.Warned, [.. warnings.Take(MaxErrors)]);
        }

        return new DeletionVerdict(finding, located ? DeletionOutcome.Safe : DeletionOutcome.Unlocated, []);
    }

    /// <summary>
    /// The findings that have to go with <paramref name="start"/>: those whose declaration mentions
    /// a finding already in the cluster, and those that share a simple name with one - an interface
    /// member, its implementations and the overrides all do - or are operators in the same file,
    /// where <c>==</c> needs <c>!=</c>.
    /// </summary>
    /// <remarks>
    /// By name, so it errs wide: a finding pulled in that nothing connects it is still dead, and
    /// deleting it too cannot hide a break that belongs to <paramref name="start"/>. Too narrow is
    /// the unsafe direction here, since a cluster cut short reports a false positive that is not one -
    /// but it is only the oracle's verdict that is affected, never what a scan reports.
    /// </remarks>
    private static List<Finding> Cluster(Finding start, IReadOnlyList<Finding> universe, Dictionary<Finding, HashSet<string>> names)
    {
        var cluster = new List<Finding> { start };
        var queue = new Queue<Finding>([start]);
        while (queue.Count > 0)
        {
            var member = queue.Dequeue();
            var simple = SimpleName(member.Symbol);
            foreach (var other in universe)
            {
                if (cluster.Contains(other))
                {
                    continue;
                }

                var connected =
                    (names.TryGetValue(other, out var mentioned) &&
                     (mentioned.Contains(simple) ||
                      (simple.EndsWith("Attribute", StringComparison.Ordinal) && mentioned.Contains(simple[..^"Attribute".Length])))) ||
                    string.Equals(SimpleName(other.Symbol), simple, StringComparison.Ordinal) ||
                    (Nameless(other.Symbol) && Nameless(member.Symbol) && string.Equals(other.File, member.File, StringComparison.Ordinal));
                if (connected)
                {
                    cluster.Add(other);
                    queue.Enqueue(other);
                }
            }
        }

        return cluster;
    }

    private static Dictionary<Finding, HashSet<string>> IdentifiersByFinding(
        string root,
        IReadOnlyList<OracleProject> projects,
        IReadOnlyList<Finding> findings,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Finding, HashSet<string>>();
        foreach (var finding in findings)
        {
            foreach (var project in projects)
            {
                if (TreeOf(project, root, finding.File) is { } tree && Declaration(tree, finding, cancellationToken) is { } declaration)
                {
                    result[finding] = [.. declaration.DescendantTokens()
                        .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
                        .Select(token => token.ValueText)];
                    break;
                }
            }
        }

        return result;
    }

    private static SyntaxTree? TreeOf(OracleProject project, string root, string file) =>
        project.Compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Relative(root, candidate.FilePath), file, StringComparison.OrdinalIgnoreCase));

    private static void AddNew(
        OracleProject project,
        Compilation edited,
        List<string> errors,
        List<string> warnings,
        Dictionary<OracleProject, Dictionary<string, int>> baselines,
        CancellationToken cancellationToken)
    {
        if (!baselines.TryGetValue(project, out var baseline))
        {
            baseline = Count(project.Compilation, cancellationToken);
            baselines[project] = baseline;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diagnostic in edited.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            var key = Key(diagnostic);
            seen[key] = seen.GetValueOrDefault(key) + 1;

            // Matched by id and message, not position: deleting a declaration moves every line
            // under it, and an error that was there before must not read as one the deletion made.
            if (seen[key] > baseline.GetValueOrDefault(key))
            {
                (diagnostic.IsWarningAsError ? warnings : errors)
                    .Add($"{project.Name}: {diagnostic.Id} {diagnostic.GetMessage(CultureInfo.InvariantCulture)}");
            }
        }
    }

    private static Dictionary<string, int> Count(Compilation compilation, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            var key = Key(diagnostic);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private static string Key(Diagnostic diagnostic) =>
        string.Concat(diagnostic.Id, "|", diagnostic.GetMessage(CultureInfo.InvariantCulture));

    /// <summary>
    /// The dependent's reference to this very compilation. A project built for several frameworks is
    /// several compilations under one assembly name, and a dependent for one of them must not be
    /// handed another's output: it lacks that framework's references and fails for a reason the
    /// deletion has nothing to do with. Each copy is checked in its own pass over the projects.
    /// </summary>
    private static MetadataReference? Reference(Compilation compilation, Compilation referenced, string assembly) =>
        compilation.References.FirstOrDefault(reference => reference switch
        {
            CompilationReference compiled => ReferenceEquals(compiled.Compilation, referenced),
            PortableExecutableReference pe => string.Equals(
                Path.GetFileNameWithoutExtension(pe.FilePath), assembly, StringComparison.OrdinalIgnoreCase),
            _ => false,
        });

    private static string Relative(string root, string path) =>
        path.Length == 0 ? path : Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// The declaration whose name sits on the finding's line. The graph records the line of the
    /// identifier, not of the attributes or modifiers above it.
    /// </summary>
    private static SyntaxNode? Declaration(SyntaxTree tree, Finding finding, CancellationToken cancellationToken)
    {
        var text = tree.GetText(cancellationToken);
        var simple = SimpleName(finding.Symbol);

        foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
        {
            // Members with no name of their own: the finding's line is the one thing to match.
            var anchor = node switch
            {
                OperatorDeclarationSyntax @operator => @operator.OperatorToken,
                ConversionOperatorDeclarationSyntax conversion => conversion.OperatorKeyword,
                IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
                DestructorDeclarationSyntax destructor => destructor.Identifier,
                _ => default,
            };

            if (anchor != default)
            {
                if (text.Lines.GetLineFromPosition(anchor.SpanStart).LineNumber + 1 == finding.Line &&
                    Nameless(finding.Symbol))
                {
                    return node;
                }

                continue;
            }

            var identifier = node switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier,
                MethodDeclarationSyntax method => method.Identifier,
                ConstructorDeclarationSyntax constructor => constructor.Identifier,
                PropertyDeclarationSyntax property => property.Identifier,
                EventDeclarationSyntax @event => @event.Identifier,
                VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } variable => variable.Identifier,
                EnumMemberDeclarationSyntax member => member.Identifier,
                _ => default,
            };

            if (identifier == default ||
                text.Lines.GetLineFromPosition(identifier.SpanStart).LineNumber + 1 != finding.Line ||
                !string.Equals(identifier.ValueText, simple, StringComparison.Ordinal))
            {
                continue;
            }

            return node;
        }

        return null;
    }

    /// <summary><c>Type.Member(Args)</c> and <c>Ns.Type</c> both reduce to the identifier a declaration carries.</summary>
    private static string SimpleName(string display)
    {
        var name = display;
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            name = name[..paren];
        }

        // The last dot outside angle brackets: `CustomList<T>.Enumerator.MoveNext` must lose its
        // owners, not be cut at the first `<`. Generic arguments are dropped from the last segment only.
        var depth = 0;
        var dot = -1;
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case '.' when depth == 0:
                    dot = i;
                    break;
            }
        }

        var last = dot >= 0 ? name[(dot + 1)..] : name;
        var generic = last.IndexOf('<', StringComparison.Ordinal);
        return generic >= 0 ? last[..generic] : last;
    }

    /// <summary>Operators, conversions, indexers and destructors: names the display form spells out in words or symbols.</summary>
    private static bool Nameless(string display) =>
        display.Contains("operator ", StringComparison.Ordinal) ||
        display.Contains("this[", StringComparison.Ordinal) ||
        display.Contains('~', StringComparison.Ordinal);

    private static SyntaxTree RemoveDeclarations(SyntaxTree tree, IReadOnlyList<SyntaxNode> declarations)
    {
        var root = tree.GetRoot();

        // `int a, b;` loses one declarator; `int a;` loses the field, since a field with no
        // declarator does not parse. A declaration inside another one that is going anyway is
        // dropped from the list: removing both is an error to the syntax API.
        var targets = declarations
            .Select(declaration => declaration is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: { } field } }
                ? field
                : declaration)
            .Distinct()
            .ToList();
        targets = [.. targets.Where(target => !targets.Any(other => other != target && other.Span.Contains(target.Span)))];

        var edited = root.RemoveNodes(targets, SyntaxRemoveOptions.KeepNoTrivia) ?? root;
        return tree.WithRootAndOptions(edited, tree.Options);
    }
}
