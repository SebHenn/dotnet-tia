using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Tia.DeadCode;

/// <summary>When the deletion oracle may measure at all.</summary>
public static class OracleGate
{
    /// <summary>The projects whose own compilation already has errors, with how many.</summary>
    /// <remarks>
    /// A deletion is judged by the errors it adds, and a project that does not compile before the
    /// deletion has errors the verdict can mistake for, or hide behind, the deletion's own.
    /// </remarks>
    public static IReadOnlyList<string> BrokenBaselines(IEnumerable<OracleProject> projects, CancellationToken cancellationToken = default) =>
        [.. projects
            .Select(project => (project.Name, Errors: project.Compilation.GetDiagnostics(cancellationToken).Count(d => d.Severity == DiagnosticSeverity.Error)))
            .Where(project => project.Errors > 0)
            .Select(project => string.Create(CultureInfo.InvariantCulture, $"{project.Name} has {project.Errors} errors before any deletion"))];

    /// <summary>The reason to stop, or null when the oracle may measure.</summary>
    /// <param name="loadProblems">Why part of the solution did not load.</param>
    /// <param name="brokenBaselines">From <see cref="BrokenBaselines"/>.</param>
    /// <param name="allowPartial">The caller accepts an optimistic figure over what did load and compile.</param>
    public static string? Refusal(IReadOnlyList<string> loadProblems, IReadOnlyList<string> brokenBaselines, bool allowPartial)
    {
        if (allowPartial || (loadProblems.Count == 0 && brokenBaselines.Count == 0))
        {
            return null;
        }

        var reasons = string.Join(Environment.NewLine, loadProblems.Concat(brokenBaselines).Select(reason => $"  x {reason}"));
        return "Nothing can be concluded: the solution did not load and compile in full. Pass --allow-partial to measure the projects that did."
            + Environment.NewLine + reasons;
    }
}
