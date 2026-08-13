using Tia.Workspace;

namespace Tia.Integration.Tests;

/// <summary>
/// Reading a project's properties as MSBuild computes them rather than as they are spelled.
/// </summary>
/// <remarks>
/// The XML read this replaced honoured no conditions and expanded no functions, so it disagreed
/// with the build in both directions: it reported properties MSBuild does not set, and reported
/// unexpanded expressions as though they were values. Either can pick the wrong runner, and a
/// wrong runner means a filter in a syntax the runner does not understand - which selects nothing.
/// </remarks>
public sealed class PropertyEvaluationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tia-eval-" + Guid.NewGuid().ToString("n"));

    public PropertyEvaluationTests()
    {
        Directory.CreateDirectory(_root);
        WorkspaceLoader.RegisterMSBuild();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string WriteProject(string body)
    {
        var path = Path.Combine(_root, "Probe.csproj");
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            {body}
            </Project>
            """);

        return path;
    }

    /// <summary>
    /// The dangerous direction. A property group whose condition is false contributes nothing to
    /// the build, but the XML read reported its contents anyway - so a project that does not opt
    /// into Microsoft.Testing.Platform could be detected as though it had.
    /// </summary>
    [Fact]
    public void A_property_behind_a_false_condition_is_not_set()
    {
        var path = WriteProject("""
              <PropertyGroup Condition="'$(SomethingUnset)' == 'yes'">
                <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
              </PropertyGroup>
            """);

        var evaluated = MsBuildPropertyProbe.ReadEvaluated(path, _root);

        Assert.True(evaluated.Evaluated);
        Assert.False(evaluated.Values.ContainsKey("UseMicrosoftTestingPlatformRunner"));

        // The old behaviour, kept as the fallback, is why this test is worth having: it still
        // reports the property, because it never looked at the condition.
        Assert.Equal("true", MsBuildPropertyProbe.Read(path, _root).Values["UseMicrosoftTestingPlatformRunner"]);
    }

    [Fact]
    public void A_property_behind_a_true_condition_is_set()
    {
        var path = WriteProject("""
              <PropertyGroup>
                <Switch>yes</Switch>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Switch)' == 'yes'">
                <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
              </PropertyGroup>
            """);

        Assert.Equal("true", MsBuildPropertyProbe.ReadEvaluated(path, _root).Values["UseMicrosoftTestingPlatformRunner"]);
    }

    /// <summary>
    /// The other direction: the XML read reports the expression itself, and
    /// <c>$([System.String]::Copy('true'))</c> is not equal to <c>true</c> by any comparison the
    /// detector makes, so the property was effectively invisible.
    /// </summary>
    [Fact]
    public void A_property_function_is_expanded()
    {
        var path = WriteProject("""
              <PropertyGroup>
                <EnableNUnitRunner>$([System.String]::Copy('true'))</EnableNUnitRunner>
              </PropertyGroup>
            """);

        Assert.Equal("true", MsBuildPropertyProbe.ReadEvaluated(path, _root).Values["EnableNUnitRunner"]);
        Assert.Equal(
            "$([System.String]::Copy('true'))",
            MsBuildPropertyProbe.Read(path, _root).Values["EnableNUnitRunner"]);
    }

    /// <summary>
    /// A property the SDK supplies a default for, which the project file never writes. The XML read
    /// sees nothing at all here, because there is nothing written to see; evaluation gets the value
    /// the build would actually use.
    /// </summary>
    /// <remarks>
    /// <c>OutputType</c> stands in for the whole class, of which the one that matters is
    /// <c>UsingMicrosoftNETSdkTest</c> - tested by <c>FrameworkDetector</c> since long before this
    /// probe evaluated anything, against a dictionary that could not contain it. That one is set by
    /// Microsoft.NET.Test.Sdk's own props rather than by the SDK proper, so it appears only when
    /// evaluating a *restored* project that references the package, which is more setup than this
    /// fact needs.
    /// </remarks>
    [Fact]
    public void An_sdk_supplied_default_is_visible_now()
    {
        var path = WriteProject("""
              <PropertyGroup>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
            """);

        Assert.Equal("Library", MsBuildPropertyProbe.ReadEvaluated(path, _root).Values["OutputType"]);
        Assert.False(MsBuildPropertyProbe.Read(path, _root).Values.ContainsKey("OutputType"));
    }

    /// <summary>
    /// A project evaluation cannot open must degrade to the literal read rather than to nothing.
    /// Before evaluation existed such a project was probed by XML and worked; a rethrow here would
    /// have made it strictly worse than it was.
    /// </summary>
    [Fact]
    public void An_unevaluatable_project_falls_back_to_the_xml_read()
    {
        var path = Path.Combine(_root, "Broken.csproj");
        File.WriteAllText(path, """
            <Project Sdk="This.Sdk.Does.Not.Exist">
              <PropertyGroup>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
            </Project>
            """);

        var probed = MsBuildPropertyProbe.ReadEvaluated(path, _root);

        Assert.False(probed.Evaluated);
        Assert.Equal("true", probed.Values["IsTestProject"]);
    }
}
