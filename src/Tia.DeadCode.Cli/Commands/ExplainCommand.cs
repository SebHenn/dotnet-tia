using System.CommandLine;
using System.Globalization;
using Tia.Core.Configuration;
using Tia.Workspace;

namespace Tia.DeadCode.Cli.Commands;

/// <summary>Says why a symbol is, or is not, reported as dead.</summary>
public static class ExplainCommand
{
    public static Command Create()
    {
        var symbol = new Argument<string>("symbol")
        {
            Description = "The symbol as the report prints it, e.g. Program.Helper(). A trailing part is enough.",
        };

        var path = new Option<string>("--path", "-p")
        {
            Description = "Directory of the repository.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };

        var solution = new Option<string?>("--solution", "-s")
        {
            Description = "Solution or project to load. Discovered in --path when omitted.",
        };

        var includePublic = new Option<bool>("--include-public")
        {
            Description = "Treat public members of libraries as unreached roots do in a scan with this flag.",
        };

        var command = new Command("explain", "Print the chain of references that keeps a symbol alive, or say nothing reaches it.");
        command.Arguments.Add(symbol);
        command.Options.Add(path);
        command.Options.Add(solution);
        command.Options.Add(includePublic);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(path)!);
            var solutionPath = parseResult.GetValue(solution);

            DeadCodeConfig config;
            SolutionGraph graph;
            try
            {
                config = DeadCodeConfig.Load(root);
                graph = await SolutionGraph.LoadAsync(
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

            // A project that did not load contributes no uses, so "nothing reaches it" would be a
            // claim about code this load never saw.
            if (graph.Problems.Count > 0)
            {
                Console.Error.WriteLine("Nothing can be concluded: the solution did not load in full.");
                return ExitCodes.Inconclusive;
            }

            var result = ScanCommand.Sweep(graph, parseResult.GetValue(includePublic) || config.IncludePublic, cancellationToken);
            var wanted = parseResult.GetValue(symbol)!;
            var matches = graph.Graph.Nodes.Values
                .Where(node => node.FilePath is { Length: > 0 } && Matches(node.DisplayName, wanted))
                .OrderBy(node => node.DisplayName, StringComparer.Ordinal)
                .ThenBy(node => node.ProjectName, StringComparer.Ordinal)
                .ToList();

            if (matches.Count == 0)
            {
                Console.Error.WriteLine($"No declaration in the loaded solution is called '{wanted}'.");
                return ExitCodes.Inconclusive;
            }

            // One name can be several declarations: an overload, or a file built for several
            // frameworks. Each is explained, so a live copy cannot hide a dead one.
            var unreached = false;
            foreach (var node in matches)
            {
                var chain = result.Chain(node.Key);
                var file = Path.GetRelativePath(root, node.FilePath!).Replace('\\', '/');
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{node.DisplayName}  ({file}:{node.Line}, {node.ProjectName})"));
                if (chain.Count == 0)
                {
                    unreached = true;
                    Console.Out.WriteLine("  nothing reaches it from an entry point, a test or a root.");
                    continue;
                }

                foreach (var (key, why) in chain)
                {
                    var name = graph.Graph.TryGetNode(key)?.DisplayName ?? key;
                    Console.Out.WriteLine(why is null or { Length: 0 } ? $"  {name}  (root)" : $"  {why} {name}");
                }
            }

            return unreached ? ExitCodes.Findings : ExitCodes.Clean;
        });

        return command;
    }

    private static bool Matches(string displayName, string wanted) =>
        string.Equals(displayName, wanted, StringComparison.Ordinal)
        || displayName.EndsWith("." + wanted, StringComparison.Ordinal);
}
