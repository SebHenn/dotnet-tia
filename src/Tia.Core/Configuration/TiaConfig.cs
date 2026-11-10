using System.Globalization;
using System.Text.Json;

namespace Tia.Core.Configuration;

/// <summary>A <c>tia.json</c> that cannot be read as one. Nothing is analysed under a config that was guessed at.</summary>
public sealed class ConfigFileException(string message) : Exception(message);

/// <summary>
/// What a repository tells <c>tia</c> that the graph cannot (#170): tests to run every time, paths
/// that force a full run, paths that select nothing, and defaults for the flags every CI job
/// would otherwise repeat.
/// </summary>
/// <remarks>
/// <para>
/// Read from <c>tia.json</c> in the directory <c>--path</c> names, which is the repository root
/// unless told otherwise. Path patterns are relative to that directory.
/// </para>
/// <para>
/// Strict, because a typo here is silent otherwise: <c>ignorPaths</c> would ignore nothing and
/// <c>fullRunPath</c> would force nothing, and both would look like they worked. An unknown key, a
/// value of the wrong type or a pattern that cannot match is refused, and the command exits 2.
/// </para>
/// </remarks>
public sealed record TiaConfig
{
    public const string FileName = "tia.json";

    /// <summary>No file: every rule is empty and every default is the flag's own.</summary>
    public static TiaConfig None { get; } = new();

    /// <summary>The file this was read from, or null for <see cref="None"/>.</summary>
    public string? FilePath { get; init; }

    /// <summary>Tests selected on every selective run, whatever the diff reaches.</summary>
    public IReadOnlyList<TestNamePattern> AlwaysRun { get; init; } = [];

    /// <summary>A changed file matching one of these runs the whole suite.</summary>
    public IReadOnlyList<PathPattern> FullRunPaths { get; init; } = [];

    /// <summary>
    /// A changed file matching one of these is left out of the diff, unless a full-run path also
    /// matches it. This only ever narrows, so every file it left out is reported.
    /// </summary>
    public IReadOnlyList<PathPattern> IgnorePaths { get; init; } = [];

    public ConfigDefaults Defaults { get; init; } = new();

    /// <summary>
    /// The config in <paramref name="directory"/>, or <see cref="None"/> when it has no
    /// <c>tia.json</c>.
    /// </summary>
    /// <exception cref="ConfigFileException">The file exists and is not a valid config.</exception>
    public static TiaConfig Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return None;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigFileException($"{path} could not be read: {ex.Message}");
        }

        return Parse(text, path);
    }

    /// <exception cref="ConfigFileException">Not a valid config.</exception>
    public static TiaConfig Parse(string json, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new ConfigFileException($"{path} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigFileException($"{path} has to be a JSON object.");
            }

            var config = new TiaConfig { FilePath = path };
            foreach (var property in root.EnumerateObject())
            {
                config = property.Name switch
                {
                    "$schema" => config,
                    "alwaysRun" => config with { AlwaysRun = Patterns(path, property, TestNamePattern.Parse) },
                    "fullRunPaths" => config with { FullRunPaths = Patterns(path, property, PathPattern.Parse) },
                    "ignorePaths" => config with { IgnorePaths = Patterns(path, property, PathPattern.Parse) },
                    "defaults" => config with { Defaults = ConfigDefaults.Read(path, property.Value) },
                    _ => throw Unknown(path, property.Name, string.Empty, ["alwaysRun", "fullRunPaths", "ignorePaths", "defaults"]),
                };
            }

            return config;
        }
    }

    internal static ConfigFileException Unknown(string path, string name, string scope, IEnumerable<string> known) =>
        new($"{path}: '{scope}{name}' is not a setting. Known: {string.Join(", ", known)}.");

    private delegate T? PatternParser<T>(string text, out string? error);

    private static List<T> Patterns<T>(string path, JsonProperty property, PatternParser<T> parse)
        where T : class
    {
        if (property.Value.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigFileException($"{path}: '{property.Name}' has to be an array of strings.");
        }

        var patterns = new List<T>();
        foreach (var item in property.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ConfigFileException($"{path}: '{property.Name}' has to be an array of strings, and holds {item.ValueKind.ToString().ToLowerInvariant()} {item.GetRawText()}.");
            }

            var text = item.GetString()!;
            patterns.Add(parse(text, out var error)
                         ?? throw new ConfigFileException($"{path}: '{text}' in '{property.Name}' {error}."));
        }

        return patterns;
    }
}

/// <summary>
/// Values for flags that are otherwise repeated in every invocation. A flag on the command line
/// wins over its default here.
/// </summary>
public sealed record ConfigDefaults
{
    /// <summary><c>--solution</c>, relative to the directory holding <c>tia.json</c>.</summary>
    public string? Solution { get; init; }

    /// <summary><c>--base</c>.</summary>
    public string? Base { get; init; }

    /// <summary><c>--default-branch</c>.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary><c>--cache-dir</c>.</summary>
    public string? CacheDir { get; init; }

    /// <summary><c>--coverage-threshold</c>.</summary>
    public double? CoverageThreshold { get; init; }

    /// <summary><c>--max-filter-length</c>.</summary>
    public int? MaxFilterLength { get; init; }

    private static readonly string[] Known = ["solution", "base", "defaultBranch", "cacheDir", "coverageThreshold", "maxFilterLength"];

    internal static ConfigDefaults Read(string path, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigFileException($"{path}: 'defaults' has to be an object.");
        }

        var defaults = new ConfigDefaults();
        foreach (var property in element.EnumerateObject())
        {
            defaults = property.Name switch
            {
                "solution" => defaults with { Solution = Text(path, property) },
                "base" => defaults with { Base = Text(path, property) },
                "defaultBranch" => defaults with { DefaultBranch = Text(path, property) },
                "cacheDir" => defaults with { CacheDir = Text(path, property) },
                "coverageThreshold" => defaults with
                {
                    CoverageThreshold = Number<double>(path, property, p => p.TryGetDouble(out var v) && v is >= 0 and <= 1 ? v : null, "a fraction between 0 and 1"),
                },
                "maxFilterLength" => defaults with
                {
                    MaxFilterLength = Number<int>(path, property, p => p.TryGetInt32(out var v) && v > 0 ? v : null, "a whole number greater than zero"),
                },
                _ => throw TiaConfig.Unknown(path, property.Name, "defaults.", Known),
            };
        }

        return defaults;
    }

    private static string Text(string path, JsonProperty property) =>
        property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } text
            ? text
            : throw new ConfigFileException($"{path}: 'defaults.{property.Name}' has to be a non-empty string.");

    private static T Number<T>(string path, JsonProperty property, Func<JsonElement, T?> read, string expectation)
        where T : struct =>
        property.Value.ValueKind == JsonValueKind.Number && read(property.Value) is { } value
            ? value
            : throw new ConfigFileException(string.Create(CultureInfo.InvariantCulture,
                $"{path}: 'defaults.{property.Name}' is {property.Value.GetRawText()}. Expected {expectation}."));
}
