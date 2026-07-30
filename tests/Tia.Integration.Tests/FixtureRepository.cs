using System.Reflection;
using Tia.Core.Infrastructure;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// A throwaway git repository holding a copy of the fixture solution, restored and ready to
/// analyse. Built once per test class: cloning, restoring and loading a workspace is expensive
/// enough that repeating it per test would dominate the suite.
/// </summary>
public abstract class FixtureRepository : IDisposable
{
    private readonly Dictionary<string, string> _originalContents = new(StringComparer.Ordinal);
    private readonly List<string> _created = [];
    private readonly string _solutionFileName;

    protected FixtureRepository(string metadataKey, string solutionFileName)
    {
        WorkspaceLoader.RegisterMSBuild();

        _solutionFileName = solutionFileName;
        Root = Path.Combine(Path.GetTempPath(), "tia-fixtures-" + Guid.NewGuid().ToString("n"));
        var source = ResolveFixturesDirectory(metadataKey);

        CopySources(source, Root);

        Git("init", "--initial-branch=main");
        Git("config", "user.email", "tia@example.invalid");
        Git("config", "user.name", "tia tests");
        Git("add", "-A");
        Git("commit", "-m", "fixtures");

        var restore = ProcessRunner.Run("dotnet", ["restore", SolutionPath], Root);
        if (!restore.Succeeded)
        {
            throw new InvalidOperationException("restoring the fixture solution failed: " + restore.StandardError + restore.StandardOutput);
        }
    }

    public string Root { get; }

    public string SolutionPath => Path.Combine(Root, _solutionFileName);

    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Edits a file in place, remembering its original content for teardown.</summary>
    public void Edit(string relativePath, string find, string replace)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var content = File.ReadAllText(path);

        _originalContents.TryAdd(path, content);

        var updated = content.Replace(find, replace, StringComparison.Ordinal);
        if (updated == content)
        {
            throw new InvalidOperationException($"'{find}' was not found in {relativePath}");
        }

        File.WriteAllText(path, updated);
    }

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
        {
            _originalContents.TryAdd(path, File.ReadAllText(path));
        }
        else
        {
            _created.Add(path);
        }

        File.WriteAllText(path, content);
    }

    public void Revert()
    {
        foreach (var (path, content) in _originalContents)
        {
            File.WriteAllText(path, content);
        }

        // Newly created files have to be removed, not restored: git reports them as untracked
        // additions, so leaving one behind would widen every later analysis.
        foreach (var path in _created)
        {
            File.Delete(path);
        }

        _originalContents.Clear();
        _created.Clear();
    }

    public async Task<AnalysisReport> AnalyzeAsync(bool useCache = false)
    {
        var outcome = await new SolutionAnalyzer(new AnalysisOptions
        {
            RepositoryRoot = Root,
            BaseRef = "HEAD",
            SolutionPath = SolutionPath,
            UseCache = useCache,
            FallbackFullOnError = false,
        }).AnalyzeAsync();

        return outcome.Report;
    }

    public static IReadOnlyList<string> SelectedTests(AnalysisReport report) =>
        [.. report.Projects.SelectMany(p => p.Tests).Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private void Git(params string[] args)
    {
        var result = ProcessRunner.Run("git", args, Root);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
        }
    }

    private static void CopySources(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(directory))
            {
                continue;
            }

            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            var target = file.Replace(source, destination, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        path.EndsWith($"{Path.DirectorySeparatorChar}bin", StringComparison.Ordinal) ||
        path.EndsWith($"{Path.DirectorySeparatorChar}obj", StringComparison.Ordinal);

    private static string ResolveFixturesDirectory(string metadataKey)
    {
        var value = typeof(FixtureRepository).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == metadataKey)
            .Value!;

        return Path.GetFullPath(value);
    }
}

/// <summary>The xUnit and NUnit tree: two runners on the VSTest `dotnet test` command.</summary>
public sealed class XunitFixtureRepository() : FixtureRepository("FixturesDirectory", "Fixtures.slnx");

/// <summary>
/// The TUnit tree. Separate because its global.json opts the whole repository into the
/// Microsoft.Testing.Platform `dotnet test` command, which a VSTest-bridge project cannot run
/// under - so the two runner worlds cannot share a solution.
/// </summary>
public sealed class TunitFixtureRepository() : FixtureRepository("TunitFixturesDirectory", "Fixtures.Tunit.slnx");
