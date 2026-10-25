using System.Reflection;
using Tia.Core.Infrastructure;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Validation;
using Tia.Frameworks;
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

    /// <param name="subdirectory">
    /// Places the solution below the git root, which is the ordinary shape of a monorepo. Diff
    /// paths are relative to the git root rather than to the directory being analysed, and getting
    /// that wrong matched no document at all - a silent miss rather than an error.
    /// </param>
    /// <param name="restore">
    /// False builds the one workspace this suite otherwise never sees: the state a fresh CI runner
    /// is in before anything has run. Without <c>obj/project.assets.json</c> a
    /// <c>PackageReference</c> does not resolve, so xunit's <c>[Fact]</c> binds to an error type
    /// and the suite disappears - which is a load failure and used to be reported as a result.
    /// </param>
    protected FixtureRepository(string metadataKey, string solutionFileName, string? subdirectory = null, bool restore = true)
    {
        WorkspaceLoader.RegisterMSBuild();

        _solutionFileName = solutionFileName;
        Root = Path.Combine(RealTempDirectory(), "tia-fixtures-" + Guid.NewGuid().ToString("n"));
        AnalysisRoot = subdirectory is null ? Root : Path.Combine(Root, subdirectory);
        var source = ResolveFixturesDirectory(metadataKey);

        CopySources(source, AnalysisRoot);

        Git("init", "--initial-branch=main");
        Git("config", "user.email", "tia@example.invalid");
        Git("config", "user.name", "tia tests");
        Git("add", "-A");
        Git("commit", "-m", "fixtures");

        if (!restore)
        {
            return;
        }

        var restored = ProcessRunner.Run("dotnet", ["restore", SolutionPath], Root);
        if (!restored.Succeeded)
        {
            throw new InvalidOperationException("restoring the fixture solution failed: " + restored.StandardError + restored.StandardOutput);
        }
    }

    /// <summary>The git root.</summary>
    public string Root { get; }

    /// <summary>The directory handed to <c>--path</c>, which is the git root unless the fixture
    /// was deliberately nested.</summary>
    public string AnalysisRoot { get; }

    public string SolutionPath => Path.Combine(AnalysisRoot, _solutionFileName);

    public string PathOf(params string[] parts) => Path.Combine([AnalysisRoot, .. parts]);

    /// <summary>Edits a file in place, remembering its original content for teardown.</summary>
    public void Edit(string relativePath, string find, string replace)
    {
        var path = Path.Combine(AnalysisRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
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
        var path = Path.Combine(AnalysisRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
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

    public async Task<AnalysisReport> AnalyzeAsync(bool useCache = false, bool reportSkipped = false, int? maxFilterLength = null)
    {
        var outcome = await new SolutionAnalyzer(new AnalysisOptions
        {
            RepositoryRoot = AnalysisRoot,
            BaseRef = "HEAD",
            SolutionPath = SolutionPath,
            UseCache = useCache,
            FallbackFullOnError = false,
            ReportSkipped = reportSkipped,
            MaxFilterLength = maxFilterLength,
        }).AnalyzeAsync();

        return outcome.Report;
    }

    /// <summary>
    /// Runs a project with the filter the analysis produced and reports the tests that actually
    /// executed. Asserting the argument list is not enough: a filter can be perfectly well-formed
    /// and still select nothing, which is exactly what mixing xUnit's class and method filters
    /// does.
    /// </summary>
    public IReadOnlyList<string> RunAndCollectExecuted(AnalysisReport report, string projectName)
    {
        var project = report.Projects.Single(p => p.Name == projectName);
        var resultsDirectory = Path.Combine(Path.GetTempPath(), "tia-run-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(resultsDirectory);

        using var filterFiles = new FilterFiles();

        try
        {
            var mode = TestCommandBuilder.ModeOf(report);
            // The report arguments `run --failed-first` uses, so where they go relative to the
            // filter - which differs by command, and matters once the filter is in a file - is
            // what runs here too.
            Assert.True(project.WritesTrx, $"{projectName} cannot write TRX, so what ran cannot be read back.");
            var arguments = TestCommandBuilder.Build(project, mode, [], project.FilterArguments, resultsDirectory, filterFiles);

            ProcessRunner.Run("dotnet", arguments, Root);

            return
            [
                .. Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
                    .SelectMany(TrxParser.Parse)
                    .Select(r => TrxParser.NormalizeTestName(r.Name))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];
        }
        finally
        {
            DeleteDirectory(resultsDirectory);
        }
    }

    public static IReadOnlyList<string> SelectedTests(AnalysisReport report) =>
        [.. report.Projects.SelectMany(p => p.Tests).Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        DeleteDirectory(Root);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The temporary directory with any symlinked ancestor resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// macOS puts temporary files under <c>/var</c>, which is a symlink to <c>/private/var</c>.
    /// NuGet resolves that while MSBuild passes the path through as given, so restoring a solution
    /// there writes <c>obj/*.nuget.g.props</c> under one spelling and then fails because it
    /// "already exists" under the other. Seven fixture constructions died that way on the macOS CI
    /// leg, before any of them had asserted anything.
    /// </para>
    /// <para>
    /// Resolved here rather than worked around in the engine, because no real repository lives in
    /// <c>/var</c> - the fixture chose that location and the fixture can unchoose it. How the
    /// engine handles a genuinely symlinked repository is a separate question with its own tests in
    /// <see cref="RepositoryRootTests"/>, which build the symlink deliberately instead of relying
    /// on one platform's temp directory happening to be one.
    /// </para>
    /// </remarks>
    internal static string RealTempDirectory()
    {
        // TrimEndingDirectorySeparator first: GetTempPath returns a trailing separator, and
        // GetFileName of a path ending in one is the empty string. Without the trim the walk below
        // collects no segments at all and returns the *filesystem root* - which Linux refuses to
        // write to, and which an elevated Windows runner cheerfully accepts, so the mistake passes
        // on one platform and fails on another. The guard at the end is what turns that into a
        // fallback rather than a fixture that quietly builds solutions in C:\.
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

        var segments = new Stack<string>();
        var current = temp;

        while (Path.GetDirectoryName(current) is { Length: > 0 } parent)
        {
            segments.Push(Path.GetFileName(current));
            current = parent;
        }

        // Whatever GetDirectoryName stopped at: "/" or "C:\".
        var walked = current;

        foreach (var segment in segments)
        {
            walked = Path.Combine(walked, segment);

            if (Directory.Exists(walked) &&
                new DirectoryInfo(walked).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                walked = target.FullName;
            }
        }

        var isRoot = string.Equals(walked, Path.GetPathRoot(walked), StringComparison.Ordinal);

        return !isRoot && Directory.Exists(walked) ? walked : temp;
    }

    /// <summary>
    /// Removes a temporary directory that may contain git's own object store.
    /// </summary>
    /// <remarks>
    /// git creates loose object files read-only, and on Windows <c>Directory.Delete</c> refuses to
    /// remove a read-only file. It throws <see cref="UnauthorizedAccessException"/> doing so, which
    /// does not derive from <see cref="IOException"/> - so catching only the latter let the failure
    /// escape from a collection fixture's disposal, and every test sharing that fixture was
    /// reported failed for something none of them asserted. Clearing the attribute first is what
    /// makes the delete succeed; the catch is the backstop for a file still held open.
    /// </remarks>
    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

/// <summary>The xUnit and NUnit tree, placed below the git root rather than at it.</summary>
public sealed class NestedFixtureRepository()
    : FixtureRepository("FixturesDirectory", "Fixtures.slnx", subdirectory: Path.Combine("src", "components"));

/// <summary>
/// The xUnit and NUnit tree with the restore deliberately left undone, which is what a fresh
/// runner hands the tool on the first job of a pull request.
/// </summary>
public sealed class UnrestoredFixtureRepository()
    : FixtureRepository("FixturesDirectory", "Fixtures.slnx", restore: false);
