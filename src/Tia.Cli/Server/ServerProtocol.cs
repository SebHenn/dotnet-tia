using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Workspace;

namespace Tia.Cli.Server;

/// <summary>What a client asks the server: an analysis, its status, or to stop (#76).</summary>
public sealed record ServerRequest
{
    public required string Command { get; init; }

    /// <summary>The client's tool version. A server of another version answers nothing but its own.</summary>
    public required string Version { get; init; }

    public string? BaseRef { get; init; }

    public bool ForceFull { get; init; }

    public string? DefaultBranch { get; init; }

    public bool FallbackFullOnError { get; init; } = true;

    public bool UseCache { get; init; } = true;

    public bool WriteCache { get; init; } = true;

    public string CacheDirectory { get; init; } = ".tia";

    public bool TypeFlow { get; init; }

    public int? MaxFilterLength { get; init; }

    public double CoverageThreshold { get; init; }

    public bool CheckEveryChange { get; init; }

    public bool ReportSkipped { get; init; }

    public IReadOnlyList<string> LastFailed { get; init; } = [];

    /// <summary>Everything the analysis options carry except where to analyse, which is the server's own.</summary>
    public static ServerRequest Analyze(AnalysisOptions options) => new()
    {
        Command = "analyze",
        Version = ServerProtocol.ToolVersion,
        BaseRef = options.BaseRef,
        ForceFull = options.ForceFull,
        DefaultBranch = options.DefaultBranch,
        FallbackFullOnError = options.FallbackFullOnError,
        UseCache = options.UseCache,
        WriteCache = options.WriteCache,
        CacheDirectory = options.CacheDirectory,
        TypeFlow = options.TypeFlow,
        MaxFilterLength = options.MaxFilterLength,
        CoverageThreshold = options.CoverageThreshold,
        CheckEveryChange = options.CheckEveryChange,
        ReportSkipped = options.ReportSkipped,
        LastFailed = [.. options.LastFailed],
    };

    public AnalysisOptions ToOptions(string repositoryRoot, string solutionPath, Action<string>? log) => new()
    {
        RepositoryRoot = repositoryRoot,
        SolutionPath = solutionPath,
        BaseRef = BaseRef ?? "origin/main",
        ForceFull = ForceFull,
        DefaultBranch = DefaultBranch,
        FallbackFullOnError = FallbackFullOnError,
        UseCache = UseCache,
        WriteCache = WriteCache,
        CacheDirectory = CacheDirectory,
        TypeFlow = TypeFlow,
        MaxFilterLength = MaxFilterLength,
        CoverageThreshold = CoverageThreshold,
        CheckEveryChange = CheckEveryChange,
        ReportSkipped = ReportSkipped,
        LastFailed = new HashSet<string>(LastFailed, StringComparer.Ordinal),
        Log = log,
    };
}

/// <summary>The server's answer.</summary>
public sealed record ServerResponse
{
    public required bool Ok { get; init; }

    public required string Version { get; init; }

    public string? Error { get; init; }

    public AnalysisReport? Report { get; init; }

    public IReadOnlyList<TestMethod> AllTests { get; init; } = [];

    /// <summary>What the analysis logged, for a client under <c>--verbose</c> to print.</summary>
    public IReadOnlyList<string> Log { get; init; } = [];

    /// <summary>For <c>status</c>.</summary>
    public int ProcessId { get; init; }

    public string? StartedAt { get; init; }

    public int Requests { get; init; }

    /// <summary>For <c>status</c>: how many times the workspace was loaded, the first time included.</summary>
    public int Loads { get; init; }

    /// <summary>
    /// On a version mismatch: the server is stopping because of it (#95). A server older than that
    /// change answers a mismatch and carries on, and leaves this false.
    /// </summary>
    public bool Stopping { get; init; }

    /// <summary>For <c>status</c>: whether an analysis is running now.</summary>
    public bool Busy { get; init; }

    /// <summary>For <c>status</c>: how many analyses are waiting for it.</summary>
    public int Waiting { get; init; }
}

/// <summary>Where a server for one repository and solution lives, and how its messages travel.</summary>
/// <remarks>
/// <para>
/// The pipe name is derived from the user, the repository root and the solution, never read from a
/// file. <c>.tia/server.json</c> only says that a server was started, so a client without one pays
/// nothing to find out there is none. A copy of it committed to the repository, or planted there,
/// could at worst make a client try a pipe that is not there - not point it at someone else's.
/// </para>
/// <para>
/// Both ends open the pipe as current-user-only, which on Windows is an ACL and on Unix a check of
/// the socket's owner. A server started by another account is refused by the client, and the
/// server refuses a client of another account.
/// </para>
/// <para>
/// Messages are one line of JSON each way, then the connection closes. Connections are accepted
/// while an analysis runs, and analyses are answered one at a time, in the order they arrived: the
/// server owns a single workspace and analyses are not reentrant against it (#95).
/// </para>
/// </remarks>
public static class ServerProtocol
{
    public const string StateFileName = "server.json";

    public const string VersionMismatch = "version mismatch";

    /// <summary>What to tell someone whose server is of another version (#95).</summary>
    public static string DescribeMismatch(ServerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Stopping
            ? $"the tia server here was version {response.Version} and has stopped for this one, {ToolVersion}. `dotnet tia server start` starts one of this version."
            : $"the tia server here is version {response.Version}, which does not stop for another version; it stops after its idle time, or with `dotnet tia server stop` from that version. Until then, this version ({ToolVersion}) analyses in-process.";
    }

    public static readonly JsonSerializerOptions Json = new(AnalysisReport.JsonOptions) { WriteIndented = false };

    public static string ToolVersion { get; } =
        typeof(ServerProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ServerProtocol).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string PipeName(string repositoryRoot, string solutionPath)
    {
        var identity = string.Join('\n', Environment.UserName, Normalise(repositoryRoot), Normalise(solutionPath));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "tia-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string StatePath(string cacheDirectory) => Path.Combine(cacheDirectory, StateFileName);

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message, Json) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            buffer.Write(chunk, 0, newline < 0 ? read : newline);
            if (newline >= 0)
            {
                break;
            }
        }

        return buffer.Length == 0 ? default : JsonSerializer.Deserialize<T>(buffer.ToArray(), Json);
    }

    /// <summary>Case and separators as the platform compares them, so two spellings of one path agree.</summary>
    private static string Normalise(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
