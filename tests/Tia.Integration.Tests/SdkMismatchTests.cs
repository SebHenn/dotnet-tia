using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// The one load failure that is about the toolchain rather than the project. The tool ships a
/// net9.0 asset so it installs on an SDK-9 machine (issue #13); what it cannot do there is read a
/// net10.0 project, and the raw diagnostic for that blames the target framework. These pin that a
/// mismatch is recognised and named, and - just as important - that nothing else is swallowed by
/// the same branch and reported as an SDK problem it is not.
/// </summary>
public sealed class SdkMismatchTests
{
    private static readonly Version Sdk9 = new(9, 0, 302);

    [Fact]
    public void The_netsdk1045_shape_is_recognised_and_names_both_sides()
    {
        var described = SdkMismatch.Describe(
            "Failure: NETSDK1045: The current .NET SDK does not support targeting .NET 10.0. " +
            "Either target .NET 9.0 or lower, or use a version of the .NET SDK that supports .NET 10.0.",
            Sdk9);

        Assert.NotNull(described);
        Assert.Contains("net10.0", described, StringComparison.Ordinal);
        Assert.Contains("9.0.302", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restore performed by a newer SDK leaves an assets file the older one cannot satisfy. It
    /// is the same mismatch wearing a different error code, and it reaches the user as the same
    /// dead end, so it gets the same explanation.
    /// </summary>
    [Fact]
    public void The_assets_file_shape_is_recognised_too()
    {
        var described = SdkMismatch.Describe(
            "Failure: NETSDK1005: Assets file 'obj/project.assets.json' doesn't have a target for 'net10.0'.",
            Sdk9);

        Assert.NotNull(described);
        Assert.Contains("net10.0", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The error codes are matched before the English prose because the prose is localised - this
    /// machine is de-DE, and a match that only works in English is a match that only works in CI.
    /// </summary>
    [Fact]
    public void A_localised_message_still_matches_on_its_error_code()
    {
        var described = SdkMismatch.Describe(
            "Failure: NETSDK1045: Das aktuelle .NET SDK unterstützt .NET 10.0 nicht als Ziel.",
            Sdk9);

        Assert.NotNull(described);
        Assert.Contains("net10.0", described, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrelated_failure_is_left_alone()
    {
        Assert.Null(SdkMismatch.Describe(
            "Failure: Msbuild failed when processing the file 'Broken.csproj' with message: unexpected token.",
            Sdk9));
    }

    /// <summary>
    /// Registration can fail before a version is known, and a reason that says "the registered
    /// MSBuild null" would be worse than one that simply does not name it.
    /// </summary>
    [Fact]
    public void An_unknown_sdk_version_still_produces_a_usable_reason()
    {
        var described = SdkMismatch.Describe("NETSDK1045: does not support targeting .NET 10.0", registeredVersion: null);

        Assert.NotNull(described);
        Assert.DoesNotContain("null", described, StringComparison.OrdinalIgnoreCase);
    }
}
