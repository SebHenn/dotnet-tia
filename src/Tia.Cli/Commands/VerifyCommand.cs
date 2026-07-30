using System.CommandLine;
using System.Diagnostics;
using Tia.Core.Infrastructure;
using Tia.Core.Model;
using Tia.Core.Reporting;
using Tia.Core.Validation;
using Tia.Workspace;

namespace Tia.Cli.Commands;

/// <summary>
/// The correctness harness. Injects a mutation, selects against it, then runs the whole suite:
/// any test that fails but was not selected is a miss, and a miss is the only fatal defect class
/// this tool has. Zero misses is the merge gate.
/// </summary>
public static class VerifyCommand
{
    public static Command Create(CommonOptions common)
    {
        var mutate = new Option<int>("--mutate")
        {
            Description = "Number of mutation samples to run.",
            DefaultValueFactory = _ => 25,
        };

        var seed = new Option<int>("--seed")
        {
            Description = "Random seed, so a failing run can be replayed.",
            DefaultValueFactory = _ => Environment.TickCount,
        };

        var command = new Command("verify", "Prove the selection never misses a failing test, by mutation.");
        common.AddTo(command);
        command.Options.Add(mutate);
        command.Options.Add(seed);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = common.Read(parseResult, parseResult.GetValue(common.Verbose) ? Console.Error.WriteLine : null) with
            {
                // The mutation lives in the working tree, so HEAD is the base by construction.
                BaseRef = "HEAD",
                DefaultBranch = null,
                ForceFull = false,
            };

            var samples = parseResult.GetValue(mutate);
            var random = new Random(parseResult.GetValue(seed));

            return await RunAsync(options, samples, random, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(AnalysisOptions options, int samples, Random random, CancellationToken cancellationToken)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine("  Surveying the solution...");

        var survey = await new SolutionAnalyzer(options with { ForceFull = true }).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        var testProjects = survey.Report.Projects;

        if (testProjects.Count == 0)
        {
            Console.Error.WriteLine("  No test projects were found.");
            return 1;
        }

        var candidates = CollectMutationCandidates(survey.Projects);
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("  No production source files were found to mutate.");
            return 1;
        }

        Console.Out.WriteLine($"  {candidates.Count} candidate file(s), {survey.AllTests.Count} test(s), {samples} sample(s)");
        Console.Out.WriteLine();

        var engine = new MutationEngine();
        var misses = 0;
        var usable = 0;
        var skipped = 0;

        for (var sample = 1; sample <= samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = candidates[random.Next(candidates.Count)];
            var original = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var mutation = engine.TryMutate(file, original, random);

            if (mutation is null)
            {
                skipped++;
                continue;
            }

            try
            {
                await File.WriteAllTextAsync(file, mutation.MutatedText, cancellationToken).ConfigureAwait(false);

                var relative = Path.GetRelativePath(options.RepositoryRoot, file);
                Console.Out.WriteLine($"  [{sample}/{samples}] {mutation.Member} {mutation.Description}  ({relative}:{mutation.Line})");

                var outcome = await new SolutionAnalyzer(options).AnalyzeAsync(cancellationToken).ConfigureAwait(false);

                if (outcome.Report.IsFullRun)
                {
                    Console.Out.WriteLine("           full run - the mutation is outside selection's scope, nothing to check");
                    skipped++;
                    continue;
                }

                var selected = new HashSet<string>(
                    outcome.Report.Projects.SelectMany(p => p.Tests),
                    StringComparer.Ordinal);

                var unfilteredProjects = new HashSet<string>(
                    outcome.Report.Projects.Where(p => !p.Filtered).Select(p => p.Name),
                    StringComparer.Ordinal);

                var failures = RunFullSuite(testProjects, options.RepositoryRoot, cancellationToken);
                usable++;

                var sampleMisses = new List<string>();
                foreach (var failure in failures)
                {
                    if (unfilteredProjects.Contains(failure.Project))
                    {
                        continue;
                    }

                    var normalized = TrxParser.NormalizeTestName(failure.Test);
                    if (!selected.Any(s => normalized.EndsWith(s, StringComparison.Ordinal) || s.EndsWith(normalized, StringComparison.Ordinal)))
                    {
                        sampleMisses.Add(failure.Test);
                    }
                }

                if (sampleMisses.Count == 0)
                {
                    Console.Out.WriteLine($"           OK  {failures.Count} failing test(s), all selected " +
                                          $"({outcome.Report.SelectedTests}/{outcome.Report.TotalTests} selected)");
                }
                else
                {
                    misses += sampleMisses.Count;
                    Console.Out.WriteLine($"           MISS  {sampleMisses.Count} failing test(s) were not selected:");
                    foreach (var miss in sampleMisses.Take(10))
                    {
                        Console.Out.WriteLine($"             - {miss}");
                    }
                }
            }
            finally
            {
                await File.WriteAllTextAsync(file, original, CancellationToken.None).ConfigureAwait(false);
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"  {usable} usable sample(s), {skipped} skipped, {misses} miss(es)");
        Console.Out.WriteLine(misses == 0
            ? "  PASS - no failing test was left out of a selection."
            : "  FAIL - a failing test was not selected. This is the one defect class that matters.");
        Console.Out.WriteLine();

        return misses == 0 ? 0 : 1;
    }

    private static List<string> CollectMutationCandidates(IReadOnlyList<ProjectDescriptor> projects)
    {
        var files = new List<string>();

        foreach (var project in projects.Where(p => !p.IsTestProject))
        {
            if (!Directory.Exists(project.Directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(project.Directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add(file);
            }
        }

        return files;
    }

    private static List<(string Project, string Test)> RunFullSuite(
        IReadOnlyList<ProjectSelection> projects,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var failures = new List<(string, string)>();

        foreach (var project in projects)
        {
            var resultsDirectory = Path.Combine(Path.GetTempPath(), "tia-verify-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(resultsDirectory);

            try
            {
                var arguments = new List<string> { "test", project.ProjectPath };

                if (Enum.TryParse<TestRunner>(project.Runner, out var runner) && runner == TestRunner.MicrosoftTestingPlatform)
                {
                    arguments.AddRange(["--", "--report-trx", "--results-directory", resultsDirectory]);
                }
                else
                {
                    arguments.AddRange(["--logger", "trx", "--results-directory", resultsDirectory]);
                }

                ProcessRunner.Run("dotnet", arguments, workingDirectory, cancellationToken);

                foreach (var trx in Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories))
                {
                    foreach (var failed in TrxParser.FailedTests(trx))
                    {
                        failures.Add((project.Name, failed));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"           could not collect results for {project.Name}: {ex.Message}");
            }
            finally
            {
                TryDelete(resultsDirectory);
            }
        }

        return failures;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            Debug.WriteLine($"could not clean up {directory}");
        }
    }
}
