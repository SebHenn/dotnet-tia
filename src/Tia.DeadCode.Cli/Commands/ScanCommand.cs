using System.CommandLine;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Tia.Core.Configuration;
using Tia.Workspace;

namespace Tia.DeadCode.Cli.Commands;

/// <summary>Reports what a solution no longer uses.</summary>
public static class ScanCommand
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

        var verbose = new Option<bool>("--verbose", "-v")
        {
            Description = "Print what the load and the graph build are doing.",
        };

        var includePublic = new Option<bool>("--include-public")
        {
            Description = "Also report public members of libraries that nothing in the solution uses.",
        };

        var json = new Option<bool>("--json")
        {
            Description = "Write the report as JSON to standard output.",
        };

        var command = new Command("scan", "Report code nothing reaches from an entry point or a test.");
        command.Options.Add(includePublic);
        command.Options.Add(json);
        command.Options.Add(path);
        command.Options.Add(solution);
        command.Options.Add(verbose);

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
                    parseResult.GetValue(verbose) ? Console.Error.WriteLine : null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SetupException or AmbiguousSolutionException or ConfigFileException)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCodes.Inconclusive;
            }

            // A project that is missing from the graph contributes no references, so everything
            // only it uses would be reported as dead.
            if (solutionGraph.Problems.Count > 0)
            {
                Console.Error.WriteLine("Nothing can be concluded: the solution did not load in full.");
                foreach (var problem in solutionGraph.Problems)
                {
                    Console.Error.WriteLine($"  x {problem}");
                }

                return ExitCodes.Inconclusive;
            }

            var report = Scan(root, solutionGraph, config, parseResult.GetValue(includePublic) || config.IncludePublic, cancellationToken);

            if (parseResult.GetValue(json))
            {
                Console.Out.WriteLine(report.ToJson());
            }
            else
            {
                Print(report);
            }

            return report.Findings.Count == 0 ? ExitCodes.Clean : ExitCodes.Findings;
        });

        return command;
    }

    /// <summary>Gathers the roots a loaded solution has, sweeps from them and reports the rest.</summary>
    public static DeadCodeReport Scan(
        string root,
        SolutionGraph solutionGraph,
        DeadCodeConfig config,
        bool includePublic,
        CancellationToken cancellationToken = default)
    {
        var graph = solutionGraph.Graph;
        var roots = Reachability.EntryPoints(graph)
            .Concat(solutionGraph.Tests.SelectMany(test => new[] { test.SymbolKey, test.ClassKey }))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var project in solutionGraph.Projects)
        {
            roots.UnionWith(CompilationRoots.AssemblyAttributes(project.Compilation));
            roots.UnionWith(CompilationRoots.InheritedInterfaceImplementations(project.Compilation, cancellationToken));

            // A library's public members are called by assemblies that are not in the solution.
            // An executable's and a test project's are called by nobody.
            if (!includePublic &&
                !project.Descriptor.IsTestProject &&
                project.Compilation.Options.OutputKind is OutputKind.DynamicallyLinkedLibrary or OutputKind.NetModule)
            {
                roots.UnionWith(CompilationRoots.PublicApi(project.Compilation, cancellationToken));
            }
        }

        var result = Reachability.Sweep(graph, roots, cancellationToken);
        return DeadCodeReport.Create(root, graph, result, config, roots.Count, solutionGraph.Tests.Count);
    }

    private static void Print(DeadCodeReport report)
    {
        foreach (var file in report.Findings.GroupBy(finding => finding.File, StringComparer.Ordinal))
        {
            Console.Out.WriteLine(file.Key);
            foreach (var finding in file)
            {
                Console.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {finding.Line,5}  {finding.Kind,-8} {finding.Symbol}"));
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{report.Findings.Count:N0} unreached of {report.Declarations:N0} declarations ({report.Roots:N0} roots, {report.Tests:N0} tests)"));

        if (report.Ignored > 0)
        {
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{report.Ignored:N0} more matched a pattern in {DeadCodeConfig.FileName} and are not listed"));
        }

        foreach (var kind in report.Findings.GroupBy(finding => finding.Kind, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {kind.Count(),6:N0} {kind.Key}"));
        }
    }
}
