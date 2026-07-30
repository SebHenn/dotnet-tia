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

    /// <summary>Set when the project runs source generators or analyzers that emit code, which
    /// have no file on disk to attribute a change to.</summary>
    public bool HasSourceGenerators { get; init; }

    /// <summary>Directory that owns the project file. Used to attribute non-source files.</summary>
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
}
