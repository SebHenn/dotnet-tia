using System.CommandLine;
using System.Globalization;
using Tia.Core.Configuration;
using Tia.Core.Infrastructure;
using Tia.Workspace;

namespace Tia.DeadCode.Cli.Commands;

/// <summary>Deletes the findings the deletion oracle shows to be safe, and rebuilds to be sure.</summary>
public static class FixCommand
{
    public static Command Create()
    {
        var path = new Option<string>("--path", "-p")
        {
            Description = "Directory of the repository.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };

        var solution = new Option<string?>("--solution", "-s")
        {
            Description = "Solution or project to fix. Discovered in --path when omitted.",
        };

        var includePublic = new Option<bool>("--include-public")
        {
            Description = "Also fix public members of libraries that nothing in the solution uses. Callers outside the solution break.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Say what would be deleted and write nothing.",
        };

        var allowDirty = new Option<bool>("--allow-dirty")
        {
            Description = "Fix a working tree with uncommitted changes. Without it a fix is always one `git checkout` from undone.",
        };

        var command = new Command("fix", "Delete findings that compile without them, then rebuild; put everything back if the build breaks.");
        command.Options.Add(path);
        command.Options.Add(solution);
        command.Options.Add(includePublic);
        command.Options.Add(dryRun);
        command.Options.Add(allowDirty);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(path)!);
            var preview = parseResult.GetValue(dryRun);

            if (!preview && !parseResult.GetValue(allowDirty))
            {
                var status = ProcessRunner.Run("git", ["status", "--porcelain"], root, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
                if (!status.Succeeded || status.StandardOutput.Trim().Length > 0)
                {
                    Console.Error.WriteLine("Refusing to fix: the working tree is not clean (or is not a git repository), so the fix could not be told apart from your changes. Commit, or pass --allow-dirty.");
                    return ExitCodes.Inconclusive;
                }
            }

            DeadCodeConfig config;
            string solutionPath;
            SolutionGraph? graph;
            try
            {
                config = DeadCodeConfig.Load(root);
                var requested = parseResult.GetValue(solution);
                solutionPath = SetupException.RequireSolution(root, requested is null ? null : Path.GetFullPath(requested, root));
                graph = await FullLoad.LoadAsync(root, solutionPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SetupException or AmbiguousSolutionException or ConfigFileException)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCodes.Inconclusive;
            }

            if (graph is null)
            {
                return ExitCodes.Inconclusive;
            }

            var projects = OracleCommand.ProjectsOf(graph);
            if (OracleGate.Refusal([], OracleGate.BrokenBaselines(projects, cancellationToken), allowPartial: false) is { } refusal)
            {
                Console.Error.WriteLine(refusal);
                return ExitCodes.Inconclusive;
            }

            var report = ScanCommand.Scan(root, graph, config, parseResult.GetValue(includePublic) || config.IncludePublic, cancellationToken);
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{report.Findings.Count:N0} findings; checking each by deleting it..."));

            var verdicts = DeletionOracle.Check(root, projects, report.Findings, report.Findings, cancellationToken);
            var safe = verdicts.Where(verdict => verdict.Outcome == DeletionOutcome.Safe).Select(verdict => verdict.Finding).ToList();
            var plan = DeadCodeFixer.Plan(root, projects, safe, cancellationToken);

            foreach (var finding in plan.Deleted)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  delete {finding.File}:{finding.Line} {finding.Symbol}"));
            }

            foreach (var (finding, reason) in plan.Skipped)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  keep   {finding.File}:{finding.Line} {finding.Symbol} ({reason})"));
            }

            var left = report.Findings.Count - plan.Deleted.Count;
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{plan.Deleted.Count:N0} of {report.Findings.Count:N0} deletable; {left:N0} stay."));

            if (preview || plan.Files.Count == 0)
            {
                return ExitCodes.Clean;
            }

            // Bytes, not text: a file that starts with a byte-order mark must still start with one,
            // and a revert must give back exactly what was there. A file in an encoding other than
            // UTF-8 is left alone, because text read as UTF-8 would be written back damaged.
            var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (file, text) in plan.Files)
            {
                var full = Path.GetFullPath(file, root);
                var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
                if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
                {
                    Console.Out.WriteLine($"  keep   {file} (it is not UTF-8)");
                    continue;
                }

                originals[full] = bytes;
                var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: bytes is [0xEF, 0xBB, 0xBF, ..]);
                await File.WriteAllBytesAsync(full, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)], cancellationToken).ConfigureAwait(false);
            }

            Console.Out.WriteLine($"Rewrote {originals.Count:N0} files; building to check...");
            var build = ProcessRunner.Run("dotnet", ["build", solutionPath, "--nologo", "-v", "q"], root, cancellationToken: cancellationToken);
            if (build.Succeeded)
            {
                Console.Out.WriteLine("The build passed. Review with `git diff`; tests were not run.");
                return ExitCodes.Clean;
            }

            // The oracle checked each deletion in memory; the build is what the compiler says about
            // them all together. Anything it dislikes undoes the whole fix, because naming which
            // deletion it disliked is a guess.
            foreach (var (full, text) in originals)
            {
                await File.WriteAllBytesAsync(full, text, cancellationToken).ConfigureAwait(false);
            }

            Console.Error.WriteLine("The build failed after the fix, so every file was put back:");
            foreach (var line in build.StandardOutput.Split('\n').Where(line => line.Contains("error ", StringComparison.Ordinal)).Distinct().Take(10))
            {
                Console.Error.WriteLine("  " + line.Trim());
            }

            return ExitCodes.Inconclusive;
        });

        return command;
    }
}
