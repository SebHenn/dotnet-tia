using Tia.Core.Model;
using Tia.Frameworks.Dialects;

namespace Tia.Frameworks;

public sealed record FilterPlan
{
    public required bool Filtered { get; init; }

    public string? UnfilteredReason { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

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
/// safe - it can only run more tests - and usually faster.
/// </remarks>
public static class FilterPlanner
{
    public const int DefaultMaxFilterLength = 24_000;

    public const double DefaultCoverageThreshold = 0.6;

    public static FilterPlan Plan(
        IFilterDialect dialect,
        IReadOnlyList<TestMethod> selected,
        IReadOnlyList<TestMethod> allInProject,
        int maxFilterLength = DefaultMaxFilterLength,
        double coverageThreshold = DefaultCoverageThreshold)
    {
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
                UnfilteredReason = $"selection covers {percent:0.#}% of the project; running it whole is cheaper than filtering",
            };
        }

        var arguments = dialect.BuildArguments(selected);
        var length = arguments.Sum(a => a.Length + 3);

        if (length > maxFilterLength)
        {
            return new FilterPlan
            {
                Filtered = false,
                UnfilteredReason = $"filter would be {length} characters, above the {maxFilterLength} safe command-line limit",
            };
        }

        return new FilterPlan
        {
            Filtered = true,
            Arguments = arguments,
            ExtraMatches = dialect.ExtraMatches(selected, allInProject),
        };
    }
}
