using Tia.Core.Model;

namespace Tia.Frameworks.Dialects;

/// <summary>
/// Emits the filter arguments for one runner. Filter syntax is the main integration cost of this
/// tool and the part most likely to change under it, so it lives behind this one interface.
/// </summary>
public interface IFilterDialect
{
    string Name { get; }

    /// <summary>
    /// Arguments to append to the test command. Never includes the <c>--</c> separator: whether
    /// arguments go to the runner or to <c>dotnet test</c> depends on the runner, not the dialect.
    /// </summary>
    /// <param name="allInProject">
    /// Everything the project contains, so a dialect can collapse a class whose tests are all
    /// selected into one clause. On a large suite that is the difference between a filter that
    /// fits on a command line and one that has to be abandoned.
    /// </param>
    IReadOnlyList<string> BuildArguments(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject);

    /// <summary>
    /// The tests the emitted filter matches beyond the selected set, given everything the project
    /// contains. Reported as a widening so over-selection is never silent.
    /// </summary>
    IReadOnlyList<TestMethod> ExtraMatches(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject);
}

public static class FilterDialects
{
    public static IFilterDialect For(TestFramework framework, TestRunner runner) => (framework, runner) switch
    {
        (TestFramework.TUnit, _) => new TUnitTreeNodeDialect(),
        (TestFramework.XUnitV3, TestRunner.MicrosoftTestingPlatform) => new XunitV3MtpDialect(),
        _ => new VsTestFilterDialect(),
    };
}
