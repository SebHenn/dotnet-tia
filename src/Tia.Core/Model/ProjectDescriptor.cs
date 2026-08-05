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

    /// <summary>Directory that owns the project file. Used to attribute non-source files.</summary>
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
}
