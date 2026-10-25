namespace Tia.Core.Model;

/// <summary>What the engine needs to know about a project, independent of how it was loaded.</summary>
public sealed record ProjectDescriptor
{
    public required string Name { get; init; }

    public required string AssemblyName { get; init; }

    public required string FilePath { get; init; }

    /// <summary>Absolute path of the built test assembly, when the project produces one.</summary>
    public string? OutputFilePath { get; init; }

    public required IReadOnlyList<string> ProjectReferences { get; init; }

    public bool IsTestProject { get; init; }

    public TestFramework Framework { get; init; } = TestFramework.Unknown;

    public TestRunner Runner { get; init; } = TestRunner.Unknown;

    /// <summary>Whether the runner can write TRX when asked, without failing the run (#72).</summary>
    public bool WritesTrx { get; init; }

    /// <summary>Whether the project names a RunSettings file of its own (<c>RunSettingsFilePath</c>).</summary>
    public bool HasRunSettings { get; init; }

    /// <summary>
    /// Whether MSBuild computed this project's properties, or they were read out of its XML because
    /// evaluation could not open it. The fallback sees only literals, so a conditional or
    /// SDK-supplied property is invisible there - which changes what runner detection can conclude.
    /// </summary>
    public bool PropertiesEvaluated { get; init; }

    /// <summary>
    /// The frameworks the project file declares, when they could be read. A multi-targeted project
    /// arrives as one loaded project per framework, so this is what a missing one is measured
    /// against. Empty when properties were not evaluated, which is the same statement as "unknown"
    /// and is treated as such: nothing is concluded from a count that was never taken.
    /// </summary>
    public IReadOnlyList<string> DeclaredTargetFrameworks { get; init; } = [];

    /// <summary>
    /// The framework this instance of a multi-targeted project was loaded for, or null for a
    /// project with one. <c>dotnet test</c> on the project runs every framework it declares, so this
    /// is what limits an invocation to the instance it was meant for (#67).
    /// </summary>
    public string? TargetFramework { get; init; }

    /// <summary>Directory that owns the project file. Used to attribute non-source files.</summary>
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
}
