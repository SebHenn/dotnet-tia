using System.Text.Json;

namespace Tia.Frameworks;

/// <summary>
/// Which <c>dotnet test</c> command a repository gets.
/// </summary>
/// <remarks>
/// This is a property of the repository, not of a project, and it is not the same question as
/// which runner executes the tests. A solution can run every project on Microsoft.Testing.Platform
/// through the VSTest bridge and still get the VSTest <c>dotnet test</c> command; opting into the
/// platform-native command is a separate, repository-wide choice made in <c>global.json</c>. The
/// two commands take the project and the filter in different places, so getting this wrong
/// produces an invocation that simply fails.
/// </remarks>
public enum DotnetTestMode
{
    /// <summary>`dotnet test &lt;project&gt; --filter ...`, with runner arguments after `--`.</summary>
    VsTest,

    /// <summary>`dotnet test --project &lt;project&gt; ...`, with runner arguments passed directly.</summary>
    MicrosoftTestingPlatform,
}

public static class GlobalJson
{
    /// <summary>Reads <c>test.runner</c> from a repository's global.json, or null when unset.</summary>
    public static string? ReadTestRunner(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "global.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("test", out var test) &&
                test.TryGetProperty("runner", out var runner) &&
                runner.ValueKind == JsonValueKind.String)
            {
                return runner.GetString();
            }
        }
        catch (Exception)
        {
            // A malformed global.json is the build system's problem, not ours.
        }

        return null;
    }

    public static DotnetTestMode ReadTestMode(string repositoryRoot) =>
        IsTestingPlatform(ReadTestRunner(repositoryRoot)) ? DotnetTestMode.MicrosoftTestingPlatform : DotnetTestMode.VsTest;

    public static bool IsTestingPlatform(string? runner) =>
        runner is not null &&
        runner.Replace(".", string.Empty).Equals("MicrosoftTestingPlatform", StringComparison.OrdinalIgnoreCase);
}
