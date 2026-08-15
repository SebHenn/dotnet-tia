using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Caching;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>
/// Which concrete types each member can get hold of an instance of.
/// </summary>
/// <remarks>
/// <para>
/// This exists to sharpen one number: the bound <see cref="ImpactSelector"/> puts on an upward hop.
/// Today that bound is "everything that can reach the concrete type", which counts every mention -
/// a <c>typeof</c>, a static call, a name in a doc comment's worth of syntax. Dispatch needs more
/// than a mention: to be affected by a change to <c>German.Greet</c>, a caller of
/// <c>IGreeter.Greet</c> has to be able to hold a <c>German</c>. That is what this computes.
/// </para>
/// <para>
/// The distinction is drawn with the semantic model rather than syntactically: an expression whose
/// static type is <c>German</c> <i>is</i> a German in hand, whatever produced it, while a name that
/// merely <i>denotes</i> the type - the operand of <c>typeof</c>, the receiver of a static call, an
/// entry in a base list - holds nothing. Those two cases are the entire precision this buys, and
/// <c>GetSymbolInfo</c> returning an <see cref="ITypeSymbol"/> is exactly what separates them.
/// </para>
/// <para>
/// Everything else here is a soundness closure, because the failure mode is asymmetric. A set that
/// is too large costs selection ratio; a set that is too small costs a missed test, which is the
/// only fatal defect this project has. So obtaining a type obtains its base types (a
/// <c>Baz : German</c> in hand is a German in hand), a member obtains what its type's construction
/// obtains, and anything the analysis cannot follow - reflection, <c>dynamic</c>, an instance
/// arriving from outside the graph - yields <see cref="TypeFlowIndex.Permits"/> for every type
/// rather than none.
/// </para>
/// </remarks>
public static class TypeFlow
{
    /// <summary>
    /// Every member in one compilation, with the concrete types it can obtain.
    /// </summary>
    /// <remarks>
    /// Per project and therefore cacheable, which matters because this is a second semantic pass
    /// over every tree the graph walk already bound. The join that makes the facts useful is
    /// cross-project and happens in <see cref="TypeFlowIndex.Resolve"/> after the merge, the same
    /// split <c>RouteSeeder</c> and <c>ReflectionSeeder</c> use and for the same reason: a fragment
    /// only ever sees its own project.
    /// </remarks>
    public static IReadOnlyList<TypeFlowFact> Scan(
        Compilation compilation,
        IReadOnlySet<string> trackedAssemblies,
        CancellationToken cancellationToken = default)
    {
        var scanner = new Scanner(trackedAssemblies);

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanner.WalkTree(compilation, tree, cancellationToken);
        }

        return scanner.Facts();
    }

    private sealed class Scanner(IReadOnlySet<string> trackedAssemblies)
    {
        private readonly Dictionary<string, HashSet<string>> _obtained = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unknown = new(StringComparer.Ordinal);

        public IReadOnlyList<TypeFlowFact> Facts()
        {
            var facts = new List<TypeFlowFact>(_obtained.Count + _unknown.Count);

            foreach (var (member, types) in _obtained)
            {
                facts.Add(new TypeFlowFact(member, [.. types], _unknown.Contains(member)));
            }

            foreach (var member in _unknown)
            {
                if (!_obtained.ContainsKey(member))
                {
                    facts.Add(new TypeFlowFact(member, [], true));
                }
            }

            return facts;
        }

        /// <remarks>
        /// The same explicit-stack shape as <see cref="ReferenceGraphBuilder"/>, and for the same
        /// reason: generated files nest deeply enough to overflow a pooled thread's stack, and the
        /// enclosing member is already known on the way down rather than needing an ancestor climb
        /// per node.
        /// </remarks>
        public void WalkTree(Compilation compilation, SyntaxTree tree, CancellationToken cancellationToken)
        {
            var model = compilation.GetSemanticModel(tree);
            var stack = new Stack<(SyntaxNode Node, string? TypeKey, string? MemberKey)>();
            stack.Push((tree.GetRoot(cancellationToken), null, null));

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (node, typeKey, memberKey) = stack.Pop();

                switch (node)
                {
                    case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    {
                        if (KeyOfDeclaration(model, node, cancellationToken) is { } key)
                        {
                            typeKey = key;
                            memberKey = null;
                        }

                        break;
                    }

                    case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or EnumMemberDeclarationSyntax:
                    {
                        memberKey = KeyOfDeclaration(model, node, cancellationToken) ?? memberKey;
                        break;
                    }

                    case VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax:
                    {
                        memberKey = KeyOfDeclaration(model, variable, cancellationToken) ?? memberKey;
                        break;
                    }
                }

                if ((memberKey ?? typeKey) is { } owner)
                {
                    Record(model, node, owner, cancellationToken);
                }

                foreach (var child in node.ChildNodes())
                {
                    stack.Push((child, typeKey, memberKey));
                }
            }
        }

        private string? KeyOfDeclaration(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
        {
            var declared = SymbolKeys.Normalize(model.GetDeclaredSymbol(node, cancellationToken));
            return declared is not null && IsTracked(declared) ? SymbolKeys.For(declared) : null;
        }

        private void Record(SemanticModel model, SyntaxNode node, string owner, CancellationToken cancellationToken)
        {
            if (node is not ExpressionSyntax expression)
            {
                return;
            }

            var info = model.GetSymbolInfo(expression, cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

            // A name that denotes a type holds no instance of it. `typeof(German)`, the receiver of
            // `German.Parse(...)`, the entry in a base list: all of them reach German and none of
            // them can dispatch to it. Excluding them is the whole difference between this and the
            // reachability bound it refines.
            if (symbol is ITypeSymbol or INamespaceSymbol)
            {
                return;
            }

            if (symbol is IMethodSymbol method)
            {
                // A type handed to a factory is a type the caller asked to be given:
                // `GetRequiredService<German>()`, `AddSingleton<IGreeter, German>()`,
                // `Deserialize<German>()`. Nothing in the expression's own type says so, because
                // the registration returns a service collection and the resolve returns the
                // interface.
                foreach (var argument in method.TypeArguments)
                {
                    Add(owner, argument);
                }

                foreach (var argument in method.ContainingType?.TypeArguments ?? [])
                {
                    Add(owner, argument);
                }

                // An instance of ours produced by code we cannot read, handed back as something
                // that says nothing about which implementation it is. A container resolve is the
                // ordinary case; the honest answer is that any implementation could arrive.
                if (!IsTracked(method) && YieldsUnknownImplementation(method.ReturnType))
                {
                    _unknown.Add(owner);
                }
            }

            var type = model.GetTypeInfo(expression, cancellationToken).Type;

            if (type is IDynamicTypeSymbol)
            {
                _unknown.Add(owner);
                return;
            }

            Add(owner, type);

            // A field assigned in one member and read in another is how a fixture shares state, and
            // no reference edge runs from the writer to the reader. Recording the value on the
            // field lets the reader pick it up through the edge it does have.
            if (node is not AssignmentExpressionSyntax assignment)
            {
                return;
            }

            var target = model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
            if (target is IFieldSymbol or IPropertySymbol && IsTracked(target) && SymbolKeys.For(target) is { } targetKey)
            {
                Add(targetKey, model.GetTypeInfo(assignment.Right, cancellationToken).Type);
            }
        }

        /// <summary>
        /// Records that <paramref name="owner"/> can hold a <paramref name="type"/>, and everything
        /// a <paramref name="type"/> also is.
        /// </summary>
        /// <remarks>
        /// The base closure is not an approximation, it is the definition: a <c>Baz</c> in hand is a
        /// <c>German</c> in hand when <c>Baz : German</c>, and the bound is drawn on whichever type
        /// declares the implementation that changed - which is routinely the base. Leaving it out is
        /// the shape of miss that gets a tool like this uninstalled.
        /// </remarks>
        private void Add(string owner, ITypeSymbol? type)
        {
            for (var current = Obtainable(type); current is not null; current = Obtainable(current.BaseType))
            {
                if (!IsTracked(current) || SymbolKeys.For(current) is not { } key)
                {
                    continue;
                }

                if (!_obtained.TryGetValue(owner, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _obtained[owner] = set;
                }

                set.Add(key);
            }
        }

        /// <summary>
        /// The named class or struct an expression's type stands for, or null when holding it says
        /// nothing about which implementation is in hand.
        /// </summary>
        /// <remarks>
        /// Interfaces are the deliberate omission. An <c>IGreeter</c> in hand is precisely the
        /// question the bound is asking about, so answering it with itself would make the whole
        /// analysis a no-op. Abstract classes stay: they are ordinary base types, and an
        /// implementation declared on one has the abstract class as its bound.
        /// </remarks>
        private static INamedTypeSymbol? Obtainable(ITypeSymbol? type) => type switch
        {
            IArrayTypeSymbol array => Obtainable(array.ElementType),
            INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named =>
                (INamedTypeSymbol)named.OriginalDefinition,
            _ => null,
        };

        /// <summary>
        /// Whether a call into code the graph does not track could hand back one of our types
        /// without naming which.
        /// </summary>
        private bool YieldsUnknownImplementation(ITypeSymbol? returnType)
        {
            for (var i = 0; i < 4 && returnType is not null; i++)
            {
                if (returnType is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface)
                {
                    return IsTracked(iface);
                }

                if (returnType is INamedTypeSymbol { IsAbstract: true } abstractType)
                {
                    return IsTracked(abstractType);
                }

                // A Task, a ValueTask, a sequence: the interesting type is the one inside.
                returnType = returnType switch
                {
                    IArrayTypeSymbol array => array.ElementType,
                    INamedTypeSymbol { TypeArguments.Length: 1 } wrapper => wrapper.TypeArguments[0],
                    _ => null,
                };
            }

            return false;
        }

        private bool IsTracked(ISymbol symbol) =>
            symbol.ContainingAssembly?.Identity.Name is { } assembly && trackedAssemblies.Contains(assembly);
    }
}
