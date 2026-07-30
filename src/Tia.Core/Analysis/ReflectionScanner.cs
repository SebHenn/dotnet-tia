using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tia.Core.Model;

namespace Tia.Core.Analysis;

/// <summary>A reflection construct, and the member that contains it.</summary>
public sealed record ReflectionFinding(string Description, string? OwningMemberKey);

/// <summary>
/// Finds the constructs that make a call graph incomplete: code that reaches a type or member by
/// name at runtime has edges no static walk can see.
/// </summary>
/// <remarks>
/// Findings carry the member that contains them, which is what lets the caller distinguish two
/// quite different risks. Reflection in a *changed* file is suspect wholesale - the change may
/// have altered what gets reflected on. Reflection elsewhere only matters if the member holding it
/// is actually in the impact set, because a method that will not run cannot reflect on anything.
/// </remarks>
public static class ReflectionScanner
{
    /// <summary>Members that mean reflection whatever they are called on.</summary>
    private static readonly HashSet<string> StrongMembers = new(StringComparer.Ordinal)
    {
        "CreateInstance",
        "CreateInstanceFrom",
        "GetMethod",
        "GetMethods",
        "GetTypes",
        "GetProperty",
        "GetProperties",
        "GetField",
        "GetFields",
        "GetMember",
        "GetMembers",
        "GetConstructor",
        "GetConstructors",
        "GetInterface",
        "GetInterfaces",
        "InvokeMember",
        "CreateDelegate",
        "MakeGenericType",
        "MakeGenericMethod",
        "GetExportedTypes",
    };

    /// <summary>
    /// Members that are only reflection in context. <c>Invoke</c> and <c>GetType</c> are far too
    /// common to flag on their own - a delegate invocation is not a broken call graph.
    /// </summary>
    private static readonly HashSet<string> WeakMembers = new(StringComparer.Ordinal)
    {
        "GetType",
        "Invoke",
        "Compile",
        "Load",
        "LoadFrom",
    };

    private static readonly HashSet<string> ReflectionReceivers = new(StringComparer.Ordinal)
    {
        "Activator",
        "Type",
        "Assembly",
        "MethodInfo",
        "PropertyInfo",
        "FieldInfo",
        "ConstructorInfo",
        "MemberInfo",
        "Expression",
        "AppDomain",
        "TypeDescriptor",
        "RuntimeHelpers",
    };

    /// <summary>Types that own the BCL's reflection surface.</summary>
    private static readonly HashSet<string> ReflectionDeclaringTypes = new(StringComparer.Ordinal)
    {
        "System.Type",
        "System.Activator",
        "System.AppDomain",
        "System.ComponentModel.TypeDescriptor",
        "System.Runtime.CompilerServices.RuntimeHelpers",
    };

    /// <summary>
    /// Scans a document with binding available, which removes the false positives a purely
    /// syntactic scan cannot avoid - a library's own <c>expression.GetMember()</c> extension is
    /// not <c>Type.GetMember</c>, and treating it as reflection widens whole projects for nothing.
    /// </summary>
    public static IReadOnlyList<ReflectionFinding> Scan(SemanticModel model, CancellationToken cancellationToken = default)
    {
        var findings = new List<ReflectionFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in model.SyntaxTree.GetRoot(cancellationToken).DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? description = node switch
            {
                MemberAccessExpressionSyntax access when IsReflectionAccess(access, model, cancellationToken) => Describe(access),
                IdentifierNameSyntax { Identifier.ValueText: "dynamic" } identifier when IsDynamicKeyword(identifier) => "dynamic",
                _ => null,
            };

            if (description is null || !seen.Add(description))
            {
                continue;
            }

            findings.Add(new ReflectionFinding(description, OwningMemberKey(node, model, cancellationToken)));
        }

        return findings;
    }

    /// <summary>Syntax-only overload, for callers that have no compilation - notably the tests
    /// that pin the vocabulary itself.</summary>
    public static IReadOnlyList<string> Scan(SyntaxTree tree, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? description = node switch
            {
                MemberAccessExpressionSyntax access when IsReflectionAccessSyntactically(access) => Describe(access),
                IdentifierNameSyntax { Identifier.ValueText: "dynamic" } identifier when IsDynamicKeyword(identifier) => "dynamic",
                _ => null,
            };

            if (description is not null && seen.Add(description))
            {
                findings.Add(description);
            }
        }

        return findings;
    }

    private static bool IsReflectionAccess(MemberAccessExpressionSyntax access, SemanticModel model, CancellationToken cancellationToken)
    {
        if (!IsReflectionAccessSyntactically(access))
        {
            return false;
        }

        var symbol = model.GetSymbolInfo(access, cancellationToken).Symbol;
        if (symbol?.ContainingType is not { } declaringType)
        {
            // Unresolved: keep the syntactic verdict rather than assume the call is harmless.
            return true;
        }

        var name = TypeName(declaringType);
        return ReflectionDeclaringTypes.Contains(name)
               || name.StartsWith("System.Reflection.", StringComparison.Ordinal)
               || name.StartsWith("System.Linq.Expressions.", StringComparison.Ordinal);
    }

    private static bool IsReflectionAccessSyntactically(MemberAccessExpressionSyntax access)
    {
        var member = access.Name.Identifier.ValueText;

        if (StrongMembers.Contains(member))
        {
            return true;
        }

        if (!WeakMembers.Contains(member))
        {
            return false;
        }

        var receiver = access.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax nested => nested.Name.Identifier.ValueText,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } => inner.Name.Identifier.ValueText,
            _ => null,
        };

        return receiver is not null && (ReflectionReceivers.Contains(receiver) || StrongMembers.Contains(receiver));
    }

    /// <summary>`dynamic` as a type, not a variable that happens to be called dynamic.</summary>
    private static bool IsDynamicKeyword(IdentifierNameSyntax identifier) =>
        identifier.Parent is not (MemberAccessExpressionSyntax or ArgumentSyntax or AssignmentExpressionSyntax);

    private static string? OwningMemberKey(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is not (BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax))
            {
                continue;
            }

            var declaration = current is BaseFieldDeclarationSyntax field
                ? field.Declaration.Variables.FirstOrDefault()
                : current;

            return declaration is null ? null : SymbolKeys.For(model.GetDeclaredSymbol(declaration, cancellationToken));
        }

        return null;
    }

    private static string TypeName(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        return ns is { IsGlobalNamespace: false } ? ns.ToDisplayString() + "." + type.Name : type.Name;
    }

    private static string Describe(MemberAccessExpressionSyntax access)
    {
        var line = access.SyntaxTree.GetLineSpan(access.Span).StartLinePosition.Line + 1;
        return $"{access.ToString().Trim()} (line {line})";
    }
}
