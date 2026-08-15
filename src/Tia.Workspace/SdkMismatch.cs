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
    /// The same mismatch wearing a compiler error instead of a load failure, or null when this is
    /// an ordinary unresolved-reference problem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case the load-failure path above cannot see, and the reason it went unnoticed: pointed
    /// at a <c>net10.0</c> project, MSBuild 9 does not refuse the load. It produces a project with
    /// no references resolved at all, raises no <c>Failure</c> diagnostic, and the first thing to
    /// notice is the compile check reporting <c>CS0518</c> - "Predefined type 'System.Object' is
    /// not defined or imported". The run bails out to a full run either way, so this was never
    /// unsafe; it reported the project as not compiling, which blames the project for the SDK's
    /// limitation. That is the same wrong-target complaint issue #13 was opened about.
    /// </para>
    /// <para>
    /// <c>CS0518</c> alone is not enough to conclude it. An unrestored project produces exactly the
    /// same error, and calling that an SDK mismatch would send someone to install a toolchain they
    /// already have. So the target framework has to actually outrun the SDK doing the reading, and
    /// when it does not, this returns null and the compiler's own words stand.
    /// </para>
    /// </remarks>
    public static string? DescribeUnresolved(
        string compilerErrorId,
        IReadOnlyList<string> declaredTargetFrameworks,
        Version? registeredVersion)
    {
        ArgumentNullException.ThrowIfNull(compilerErrorId);
        ArgumentNullException.ThrowIfNull(declaredTargetFrameworks);

        if (!compilerErrorId.Equals("CS0518", StringComparison.Ordinal) || registeredVersion is null)
        {
            return null;
        }

        var beyond = declaredTargetFrameworks.FirstOrDefault(f => IsBeyond(f, registeredVersion));

        // Phrased to read inside the "X does not compile (...)" the caller wraps it in, which is
        // true here and is not the useful half of the sentence.
        return beyond is null
            ? null
            : $"not one reference resolved - the registered MSBuild {registeredVersion} cannot " +
              $"load a project targeting {beyond}, so its tests cannot be reasoned about. Install " +
              "an SDK matching the project's target framework; dotnet tia itself runs on .NET 9 " +
              "and later, but it can only analyse projects the SDK it finds is able to load.";
    }

    /// <summary>
    /// Whether a target framework needs a newer SDK than the one reading it. Only
    /// <c>net</c>-prefixed monikers are judged: <c>netstandard2.0</c> and <c>net472</c> are
    /// loadable by every SDK this tool runs on, and reading their digits as a version would make
    /// <c>net472</c> look like the future.
    /// </summary>
    private static bool IsBeyond(string targetFramework, Version registeredVersion)
    {
        var moniker = MonikerPattern().Match(targetFramework);

        return moniker.Success
               && moniker.Value.Length == targetFramework.Length
               && Version.TryParse(targetFramework["net".Length..], out var framework)
               && framework.Major > registeredVersion.Major;
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
