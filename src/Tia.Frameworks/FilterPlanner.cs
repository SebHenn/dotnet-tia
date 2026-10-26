using System.Runtime.InteropServices;
using Tia.Core.Model;
using Tia.Frameworks.Dialects;

namespace Tia.Frameworks;

public sealed record FilterPlan
{
    public required bool Filtered { get; init; }

    public string? UnfilteredReason { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Too long for the command line, so passed to the runner in a file (#90).</summary>
    public bool InFile { get; init; }

    /// <summary>Tests the filter matches beyond the selection.</summary>
    public IReadOnlyList<TestMethod> ExtraMatches { get; init; } = [];
}

/// <summary>
/// Decides whether a project runs filtered or whole.
/// </summary>
/// <remarks>
/// Two cases make the filter counterproductive. Windows caps a command line at roughly 32k
/// characters, so a long enough filter simply fails to launch; and once a selection covers most of
/// a project, the filter costs more in discovery overhead than it saves. Dropping it is always
/// safe - it can only run more tests - and usually faster. The first case is now the exception:
/// a filter too long for the command line goes in a file wherever the runner can read one there
/// (#90), and is dropped only where it cannot.
/// </remarks>
public static class FilterPlanner
{
    /// <summary>
    /// How long a filter may get before it is cheaper to abandon it.
    /// </summary>
    /// <remarks>
    /// Windows caps a whole command line at 32,767 characters, which is the constraint everyone
    /// quotes - but applying it on Linux and macOS, where the limit is measured in megabytes,
    /// throws away filters that would have worked perfectly. NodaTime's 33,000-character filter is
    /// exactly that case: abandoned on a platform that could run it, so 1,734 tests ran instead of
    /// 1,037.
    /// </remarks>
    public static readonly int DefaultMaxFilterLength =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 24_000 : 200_000;

    public const double DefaultCoverageThreshold = 0.6;

    public static FilterPlan Plan(
        IFilterDialect dialect,
        IReadOnlyList<TestMethod> selected,
        IReadOnlyList<TestMethod> allInProject,
        int? maxFilterLength = null,
        double coverageThreshold = DefaultCoverageThreshold,
        TestRunner? filterFile = null)
    {
        var lengthLimit = maxFilterLength ?? DefaultMaxFilterLength;
        if (selected.Count == 0)
        {
            return new FilterPlan { Filtered = false, UnfilteredReason = "no tests selected" };
        }

        if (allInProject.Count > 0 && (double)selected.Count / allInProject.Count >= coverageThreshold)
        {
            var percent = 100.0 * selected.Count / allInProject.Count;
            return new FilterPlan
            {
                Filtered = false,
                // Invariant: this reason is reported verbatim in --json, so a machine reading it
                // should not see a decimal comma on one machine and a point on the next.
                UnfilteredReason = FormattableString.Invariant(
                    $"selection covers {percent:0.#}% of the project; running it whole is cheaper than filtering"),
            };
        }

        var arguments = dialect.BuildArguments(selected, allInProject);
        var length = ArgumentLength(arguments);

        if (length > lengthLimit)
        {
            if (filterFile is { } runner && FilterFiles.CanCarry(runner, arguments))
            {
                return new FilterPlan
                {
                    Filtered = true,
                    InFile = true,
                    Arguments = arguments,
                    ExtraMatches = dialect.ExtraMatches(selected, allInProject),
                };
            }

            return new FilterPlan
            {
                Filtered = false,
                UnfilteredReason = $"filter would be {length} characters, above the {lengthLimit} safe command-line limit for this platform, " +
                                   "and cannot be passed in a file either (a VSTest project with run settings of its own cannot take one)",
            };
        }

        return new FilterPlan
        {
            Filtered = true,
            Arguments = arguments,
            ExtraMatches = dialect.ExtraMatches(selected, allInProject),
        };
    }

    /// <summary>How long these arguments make the command line, quoting included.</summary>
    internal static int ArgumentLength(IReadOnlyList<string> arguments) => arguments.Sum(a => a.Length + 3);
}
