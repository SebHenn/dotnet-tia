using System.Reflection;
using Tia.Core.Model;
using Tia.Frameworks;

namespace Tia.Core.Tests;

/// <summary>
/// The engine and the framework layer reference the C# compiler and nothing that loads a project.
/// </summary>
/// <remarks>
/// That boundary is what lets every test in this assembly run against
/// <c>CSharpCompilation.Create</c> with no SDK resolution, and until now it was a comment in two
/// project files and a paragraph in CONTRIBUTING. A reference that crosses it compiles, passes
/// every test here, and is only noticed when the suite needs an SDK it did not need before.
/// </remarks>
public sealed class EngineBoundaryTests
{
    public static TheoryData<string> Assemblies => [typeof(ImpactGraph).Assembly.GetName().Name!, typeof(TestDiscoverer).Assembly.GetName().Name!];

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void It_references_neither_MSBuild_nor_a_Roslyn_workspace_nor_the_layers_above_it(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == assemblyName);

        var crossing = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name =>
                name.StartsWith("Microsoft.Build", StringComparison.Ordinal) ||
                name.Contains(".Workspaces", StringComparison.Ordinal) ||
                name is "Tia.Workspace" or "dotnet-tia")
            .ToList();

        Assert.Empty(crossing);
    }

    /// <summary>
    /// The compiler drops a reference nothing uses, so the check above passes for an assembly that
    /// was never loaded as well as for one that is clean. This is the half that says it looked.
    /// </summary>
    [Theory]
    [MemberData(nameof(Assemblies))]
    public void It_does_reference_the_compiler(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == assemblyName);

        Assert.Contains(assembly.GetReferencedAssemblies(), reference => reference.Name == "Microsoft.CodeAnalysis");
    }
}
