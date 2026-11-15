using System.Text.Json;
using System.Text.RegularExpressions;
using Tia.Core.Configuration;

namespace Tia.DeadCode;

/// <summary>
/// What a repository tells the scan that the graph cannot: files whose contents something outside
/// the solution uses, and symbols a framework calls by a convention the scan does not know.
/// </summary>
/// <remarks>
/// Read from <c>deadcode.json</c> in the scanned directory. Strict for the reason
/// <see cref="TiaConfig"/> is: a misspelt key would ignore nothing and look as if it worked.
/// </remarks>
public sealed record DeadCodeConfig
{
    public const string FileName = "deadcode.json";

    public static DeadCodeConfig None { get; } = new();

    /// <summary>Findings in a file matching one of these are counted and not reported.</summary>
    public IReadOnlyList<PathPattern> IgnorePaths { get; init; } = [];

    /// <summary>
    /// Findings whose name matches one of these are counted and not reported. Matched against the
    /// name as the report prints it - <c>TestApp.BuildAvaloniaApp()</c> - with <c>*</c> for any run
    /// of characters.
    /// </summary>
    public IReadOnlyList<SymbolPattern> IgnoreSymbols { get; init; } = [];

    /// <summary><c>--include-public</c>.</summary>
    public bool IncludePublic { get; init; }

    private static readonly string[] Known = ["ignorePaths", "ignoreSymbols", "includePublic"];

    /// <exception cref="ConfigFileException">The file exists and is not a valid config.</exception>
    public static DeadCodeConfig Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return None;
        }

        try
        {
            return Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigFileException($"{path} could not be read: {ex.Message}");
        }
    }

    /// <exception cref="ConfigFileException">Not a valid config.</exception>
    public static DeadCodeConfig Parse(string json, string path)
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
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigFileException($"{path} has to be a JSON object.");
            }

            var config = new DeadCodeConfig();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                config = property.Name switch
                {
                    "$schema" => config,
                    "ignorePaths" => config with
                    {
                        IgnorePaths = Strings(path, property)
                            .Select(text => PathPattern.Parse(text, out var error)
                                            ?? throw new ConfigFileException($"{path}: '{text}' in 'ignorePaths' {error}."))
                            .ToList(),
                    },
                    "ignoreSymbols" => config with
                    {
                        IgnoreSymbols = Strings(path, property)
                            .Select(text => SymbolPattern.Parse(text)
                                            ?? throw new ConfigFileException($"{path}: an entry in 'ignoreSymbols' is empty."))
                            .ToList(),
                    },
                    "includePublic" => config with
                    {
                        IncludePublic = property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? property.Value.GetBoolean()
                            : throw new ConfigFileException($"{path}: 'includePublic' has to be true or false."),
                    },
                    _ => throw new ConfigFileException(
                        $"{path}: '{property.Name}' is not a setting. Known: {string.Join(", ", Known)}."),
                };
            }

            return config;
        }
    }

    private static List<string> Strings(string path, JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Array ||
            property.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            throw new ConfigFileException($"{path}: '{property.Name}' has to be an array of strings.");
        }

        return [.. property.Value.EnumerateArray().Select(item => item.GetString()!)];
    }
}

/// <summary>A symbol name with <c>*</c> for any run of characters. Ordinal and case-sensitive, as C# is.</summary>
public sealed class SymbolPattern
{
    private readonly Regex _regex;

    private SymbolPattern(string text, Regex regex)
    {
        Text = text;
        _regex = regex;
    }

    public string Text { get; }

    /// <summary>The pattern, or null when <paramref name="text"/> is blank.</summary>
    public static SymbolPattern? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var body = string.Join(".*", trimmed.Split('*').Select(Regex.Escape));
        return new SymbolPattern(text, new Regex(
            "^" + body + "$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline,
            TimeSpan.FromSeconds(1)));
    }

    public bool Matches(string displayName) => _regex.IsMatch(displayName);
}
