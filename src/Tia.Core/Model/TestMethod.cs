namespace Tia.Core.Model;

public enum TestFramework
{
    Unknown,
    XUnitV2,
    XUnitV3,
    NUnit,
    MSTest,
    TUnit,
}

public enum TestRunner
{
    Unknown,

    /// <summary>The classic VSTest host, driven by <c>dotnet test --filter</c>.</summary>
    VsTest,

    /// <summary>Microsoft.Testing.Platform, driven by arguments after <c>--</c>.</summary>
    MicrosoftTestingPlatform,
}

/// <summary>One discovered test method. Parameterised tests are a single entry: sub-cases are
/// never selected individually, because no filter dialect expresses that reliably.</summary>
public sealed record TestMethod
{
    /// <summary>Graph key of the method as declared (a base class, for inherited tests).</summary>
    public required string SymbolKey { get; init; }

    /// <summary>Graph key of the concrete class the test runs on.</summary>
    public required string ClassKey { get; init; }

    public required string Namespace { get; init; }

    /// <summary>Class name including any nesting, using <c>+</c> as the nesting separator.</summary>
    public required string ClassName { get; init; }

    public required string MethodName { get; init; }

    public required string ProjectName { get; init; }

    public required TestFramework Framework { get; init; }

    public bool IsParameterized { get; init; }

    public string FullyQualifiedName =>
        Namespace.Length == 0 ? $"{ClassName}.{MethodName}" : $"{Namespace}.{ClassName}.{MethodName}";

    /// <summary>
    /// A test is identified in the graph by the pair (declaring method, concrete class), so an
    /// inherited base-class test contributes one entry per concrete subclass.
    /// </summary>
    public string Identity => $"{ClassKey}#{MethodName}";

    public override string ToString() => FullyQualifiedName;
}
