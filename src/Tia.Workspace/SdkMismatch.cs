using System.Text.RegularExpressions;

namespace Tia.Workspace;

/// <summary>
/// Recognises the one workspace failure that is not a broken project but a mismatched toolchain:
/// an SDK too old to load the project it was pointed at.
/// </summary>
/// <remarks>
/// The tool ships assets for net9.0 and net10.0, so it installs and runs on a machine whose only
/// SDK is 9.0 (issue #13). What it cannot do there is load a net10.0 project - MSBuild 9 has no
/// targeting pack for it - and the raw diagnostic for that says "The current .NET SDK does not
/// support targeting .NET 10.0", which reads like a complaint about the project rather than about
/// the SDK doing the reading. The failure stays a full-run trigger either way; this only replaces
/// what the run says about itself, because a reason nobody can act on is a reason wasted.
/// </remarks>
public static partial class SdkMismatch
{
    /// <summary>
    /// An explanation of <paramref name="diagnostic"/> as an SDK-version mismatch, or null when it
    /// is some other kind of failure and should be reported verbatim.
    /// </summary>
    public static string? Describe(string diagnostic, Version? registeredVersion)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (!LooksLikeMismatch(diagnostic))
        {
            return null;
        }

        var wanted = RequestedFramework(diagnostic);
        var have = registeredVersion is null
            ? "the registered MSBuild"
            : $"the registered MSBuild {registeredVersion}";

        var target = wanted is null
            ? "a target framework it does not support"
            : $"a project targeting {wanted}";

        return $"the .NET SDK cannot load {target}: {have} is older than the project needs, so the " +
               "project could not be read and its tests cannot be reasoned about. Install an SDK " +
               "matching the project's target framework. dotnet tia itself runs on .NET 9 and later, " +
               "but it can only analyse projects the SDK it finds is able to load.";
    }

    /// <summary>
    /// Both spellings of the same problem: NETSDK1045 is MSBuild refusing the target framework
    /// outright, NETSDK1005 is a restore performed by a newer SDK leaving an assets file the older
    /// one cannot satisfy. Matched on the error codes first because those are stable across the
    /// user's display language - this machine is de-DE, and the English prose is not what it sees.
    /// </summary>
    private static bool LooksLikeMismatch(string diagnostic) =>
        diagnostic.Contains("NETSDK1045", StringComparison.Ordinal) ||
        diagnostic.Contains("NETSDK1005", StringComparison.Ordinal) ||
        diagnostic.Contains("does not support targeting", StringComparison.OrdinalIgnoreCase);

    private static string? RequestedFramework(string diagnostic)
    {
        var moniker = MonikerPattern().Match(diagnostic);
        if (moniker.Success)
        {
            return moniker.Value;
        }

        var friendly = FriendlyPattern().Match(diagnostic);
        return friendly.Success ? $"net{friendly.Groups[1].Value}" : null;
    }

    [GeneratedRegex(@"net\d+\.\d+", RegexOptions.CultureInvariant)]
    private static partial Regex MonikerPattern();

    [GeneratedRegex(@"\.NET (\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FriendlyPattern();
}
