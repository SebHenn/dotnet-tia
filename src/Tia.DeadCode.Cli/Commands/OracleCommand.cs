using System.CommandLine;
using System.Globalization;
using Tia.Core.Configuration;
using Tia.Workspace;

namespace Tia.DeadCode.Cli.Commands;

/// <summary>Measures how many findings survive being deleted.</summary>
public static class OracleCommand
{
    public static Command Create()
    {
        var path = new Option<string>("--path", "-p")
        {
            Description = "Directory of the repository to scan.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };

        var solution = new Option<string?>("--solution", "-s")
        {
            Description = "Solution or project to scan. Discovered in --path when omitted.",
        };

        var includePublic = new Option<bool>("--include-public")
        {
            Description = "Also check public members of libraries that nothing in the solution uses.",
        };

        var sample = new Option<int>("--sample")
        {
            Description = "How many findings to delete, spread evenly over the list. 0 checks all of them.",
            DefaultValueFactory = _ => 50,
        };

        var allowPartial = new Option<bool>("--allow-partial")
        {
            Description = "Measure the projects that loaded even when others did not (F#, Visual Basic, test projects with no tests). The figure is optimistic: uses in the projects that did not load are not seen.",
        };

        var command = new Command("oracle", "Delete each finding in memory and report the ones that break a compilation.");
        command.Options.Add(allowPartial);
        command.Options.Add(includePublic);
        command.Options.Add(path);
        command.Options.Add(sample);
        command.Options.Add(solution);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(path)!);
            var solutionPath = parseResult.GetValue(solution);

            DeadCodeConfig config;
            SolutionGraph solutionGraph;
            try
            {
                config = DeadCodeConfig.Load(root);
                solutionGraph = await SolutionGraph.LoadAsync(
                    root,
                    solutionPath is null ? null : Path.GetFullPath(solutionPath, root),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SetupException or AmbiguousSolutionException or ConfigFileException)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCodes.Inconclusive;
            }

            // ProjectReferences holds project names, as the workspace resolved them - not paths.
            var projects = solutionGraph.Projects
                .Select(project => new OracleProject(
                    project.Name,
                    project.Compilation,
                    [.. solutionGraph.Projects
                        .Where(other => project.Descriptor.ProjectReferences.Contains(other.Descriptor.Name, StringComparer.Ordinal))
                        .Select(other => other.Compilation.AssemblyName ?? string.Empty)]))
                .ToList();

            var notCompiling = OracleGate.BrokenBaselines(projects, cancellationToken);
            var allowed = parseResult.GetValue(allowPartial);
            if (OracleGate.Refusal(solutionGraph.Problems, notCompiling, allowed) is { } refusal)
            {
                Console.Error.WriteLine(refusal);
                return ExitCodes.Inconclusive;
            }

            if (solutionGraph.Problems.Count > 0 || notCompiling.Count > 0)
            {
                // The scan refuses to call a partly loaded solution clean, and still does. Here the
                // question is only how many of the findings survive deletion, so measuring what did
                // load is useful - on the stated condition that it is an optimistic figure, because
                // a use inside a project that did not load is invisible to the scan and the oracle alike.
                Console.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"PARTIAL: {solutionGraph.Problems.Count} load problems and {notCompiling.Count} projects that do not compile; measuring the {solutionGraph.Projects.Count} projects that loaded. Uses in the others are not seen, so the figure below is optimistic."));
            }

            var report = ScanCommand.Scan(root, solutionGraph, config, parseResult.GetValue(includePublic) || config.IncludePublic, cancellationToken);

            var wanted = parseResult.GetValue(sample);
            IReadOnlyList<Finding> chosen = wanted <= 0 || wanted >= report.Findings.Count
                ? report.Findings
                : [.. Enumerable.Range(0, wanted).Select(i => report.Findings[(int)((long)i * report.Findings.Count / wanted)])];

            var linked = projects.Sum(project => project.ReferencedAssemblies.Count);
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{projects.Count} projects, {linked} project references checked against deletions"));

            var verdicts = DeletionOracle.Check(root, projects, chosen, report.Findings, cancellationToken);

            foreach (var verdict in verdicts.Where(v => v.Outcome != DeletionOutcome.Safe))
            {
                Console.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{verdict.Outcome.ToString().ToUpperInvariant(),-9} {verdict.Finding.File}:{verdict.Finding.Line} {verdict.Finding.Symbol}"));
                foreach (var error in verdict.Errors)
                {
                    Console.Out.WriteLine($"            {error}");
                }
            }

            var safe = verdicts.Count(v => v.Outcome == DeletionOutcome.Safe);
            var broken = verdicts.Count(v => v.Outcome == DeletionOutcome.Broken);
            var warned = verdicts.Count(v => v.Outcome == DeletionOutcome.Warned);
            var unlocated = verdicts.Count(v => v.Outcome == DeletionOutcome.Unlocated);
            var decided = safe + warned + broken;

            Console.Out.WriteLine();
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{verdicts.Count} of {report.Findings.Count} findings deleted: {safe} safe, {warned} only trip a warning the build promotes, {broken} broke the build, {unlocated} not located"));
            if (decided > 0)
            {
                Console.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"compile-level precision {100.0 * (safe + warned) / decided:F1}% (an upper bound: a deletion that compiles can still break at run time)"));
            }

            return broken == 0 ? ExitCodes.Clean : ExitCodes.Findings;
        });

        return command;
    }
}
