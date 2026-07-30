using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tia.Core.Analysis;

/// <summary>
/// Finds the constructs that make a call graph incomplete. A file that reaches for a type or
/// member by name at runtime has edges no static walk can see, so it is widened to project scope
/// instead of being trusted.
/// </summary>
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

    /// <summary>Returns the reflection constructs found in a syntax tree, empty when it is clean.</summary>
    public static IReadOnlyList<string> Scan(SyntaxTree tree, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? finding = node switch
            {
                MemberAccessExpressionSyntax access when IsReflectionAccess(access) => Describe(access),
                IdentifierNameSyntax { Identifier.ValueText: "dynamic" } => "dynamic",
                _ => null,
            };

            if (finding is not null && seen.Add(finding))
            {
                findings.Add(finding);
            }
        }

        return findings;
    }

    private static bool IsReflectionAccess(MemberAccessExpressionSyntax access)
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

    private static string Describe(MemberAccessExpressionSyntax access)
    {
        var line = access.SyntaxTree.GetLineSpan(access.Span).StartLinePosition.Line + 1;
        return $"{access.ToString().Trim()} (line {line})";
    }
}
