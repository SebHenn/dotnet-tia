using Tia.Core.Model;

namespace Tia.Frameworks;

/// <summary>
/// Works out which framework a test project uses and which runner will execute it. The runner
/// matters as much as the framework: the same xUnit v3 project takes completely different filter
/// arguments depending on whether it runs natively on Microsoft.Testing.Platform or over the
/// VSTest bridge.
/// </summary>
public static class FrameworkDetector
{
    public static TestFramework DetectFramework(IEnumerable<string> referencedAssemblies)
    {
        var names = new HashSet<string>(referencedAssemblies, StringComparer.OrdinalIgnoreCase);

        if (names.Contains("xunit.v3.core") || names.Contains("xunit.v3.common") || names.Contains("xunit.v3.assert"))
        {
            return TestFramework.XUnitV3;
        }

        if (names.Contains("xunit.core") || names.Contains("xunit.abstractions"))
        {
            return TestFramework.XUnitV2;
        }

        if (names.Contains("nunit.framework"))
        {
            return TestFramework.NUnit;
        }

        if (names.Contains("Microsoft.VisualStudio.TestPlatform.TestFramework"))
        {
            return TestFramework.MSTest;
        }

        if (names.Contains("TUnit.Core"))
        {
            return TestFramework.TUnit;
        }

        return TestFramework.Unknown;
    }

    /// <param name="properties">
    /// Evaluated MSBuild properties for the project, plus <c>global.json</c>'s
    /// <c>test.runner</c> value under the key <c>GlobalJsonTestRunner</c> when present.
    /// </param>
    public static TestRunner DetectRunner(
        TestFramework framework,
        IEnumerable<string> referencedAssemblies,
        IReadOnlyDictionary<string, string> properties)
    {
        var names = new HashSet<string>(referencedAssemblies, StringComparer.OrdinalIgnoreCase);

        // TUnit only ever runs on Microsoft.Testing.Platform.
        if (framework == TestFramework.TUnit)
        {
            return TestRunner.MicrosoftTestingPlatform;
        }

        if (IsTrue(properties, "UseMicrosoftTestingPlatformRunner") ||
            IsTrue(properties, "TestingPlatformDotnetTestSupport") ||
            IsTrue(properties, "EnableMSTestRunner") ||
            IsTrue(properties, "EnableNUnitRunner"))
        {
            return TestRunner.MicrosoftTestingPlatform;
        }

        if (properties.TryGetValue("GlobalJsonTestRunner", out var runner) && GlobalJson.IsTestingPlatform(runner))
        {
            return TestRunner.MicrosoftTestingPlatform;
        }

        // Microsoft.NET.Test.Sdk is what brings the VSTest bridge in; when it is present and
        // nothing opted into MTP, `dotnet test` goes through VSTest.
        if (names.Contains("Microsoft.VisualStudio.TestPlatform.ObjectModel") || IsTrue(properties, "UsingMicrosoftNETSdkTest"))
        {
            return TestRunner.VsTest;
        }

        if (names.Contains("Microsoft.Testing.Platform"))
        {
            return TestRunner.MicrosoftTestingPlatform;
        }

        return TestRunner.VsTest;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
