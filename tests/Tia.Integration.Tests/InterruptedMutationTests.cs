using System.Text;
using Tia.Cli;
using Tia.Core.Validation;
using Tia.Workspace;
using Tia.Workspace.Harness;

namespace Tia.Integration.Tests;

/// <summary>
/// What the next <c>verify</c> does about a run that was killed between writing a mutation and
/// putting the file back, and about a working tree git cannot describe (#38).
/// </summary>
/// <remarks>
/// A kill is simulated by doing exactly what a sample does before its <c>try</c> - record, then
/// write the mutation - and stopping there, which is where a SIGKILL or a CI timeout leaves it.
/// Before #38 the next run met that file as a dirty tree and told the user to commit it.
/// </remarks>
public sealed class InterruptedMutationTests : IDisposable
{
    private const string Original = "﻿class Widget\r\n{\r\n    int Add(int a, int b) => a + b;\r\n}\r\n";
    private const string Mutated = "﻿class Widget\r\n{\r\n    int Add(int a, int b) => a - b;\r\n}\r\n";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tia-sentinel-");

    public InterruptedMutationTests()
    {
        TestGit.Init(Root);
        TestGit.Run(Root, "config", "core.autocrlf", "false");

        Directory.CreateDirectory(Path.Combine(Root, "src"));
        File.WriteAllBytes(WidgetPath, Bytes(Original));
        TestGit.Run(Root, "add", "-A");
        TestGit.Run(Root, "commit", "-m", "widget");
    }

    private string Root => _directory.FullName;

    private string WidgetPath => Path.Combine(Root, "src", "Widget.cs");

    private string SentinelPath => MutationSentinel.Location(Root, ".tia");

    // Encoding.UTF8.GetBytes does not emit a preamble, so the BOM in the string is the only one.
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The state a kill between the write and the restore leaves behind.</summary>
    private void KillAfterMutating()
    {
        MutationSentinel.Write(SentinelPath, Path.Combine("src", "Widget.cs"), Bytes(Original), Bytes(Mutated));
        File.WriteAllBytes(WidgetPath, Bytes(Mutated));
    }

    [Fact]
    public void A_mutation_a_killed_run_left_behind_is_put_back_byte_for_byte()
    {
        KillAfterMutating();

        var notice = MutationSentinel.Recover(Root, ".tia");

        Assert.NotNull(notice);
        Assert.Contains("src/Widget.cs", notice, StringComparison.Ordinal);

        // Byte-order mark and CRLFs included: a restore that normalised either would be a change
        // of its own, and every later sample would analyse it.
        Assert.Equal(Bytes(Original), File.ReadAllBytes(WidgetPath));
        Assert.False(File.Exists(SentinelPath));

        // The point of recovering first: the tree is clean again, so the user is never told to
        // commit the mutation.
        MutationHarness.RequireCleanWorkingTree(Root);
    }

    /// <summary>
    /// The kill landed after the record and before the mutation, or after the restore and before
    /// the record was deleted. The file is as the run found it, so only the record goes.
    /// </summary>
    [Fact]
    public void A_record_whose_file_was_never_mutated_or_already_restored_is_just_removed()
    {
        KillAfterMutating();
        File.WriteAllBytes(WidgetPath, Bytes(Original));
        var written = File.GetLastWriteTimeUtc(WidgetPath);

        Assert.Null(MutationSentinel.Recover(Root, ".tia"));

        Assert.False(File.Exists(SentinelPath));
        Assert.Equal(Bytes(Original), File.ReadAllBytes(WidgetPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(WidgetPath));
    }

    /// <summary>
    /// Someone edited the mutated file before running again. Writing the original back would
    /// destroy that edit, and leaving it would gate a tree the harness did not make, so it refuses
    /// - and the refusal has to say how to get out, or it is a dead end.
    /// </summary>
    [Fact]
    public void A_file_changed_since_it_was_mutated_is_refused_with_the_way_out()
    {
        KillAfterMutating();
        var edited = Bytes(Mutated.Replace("a - b", "a * b", StringComparison.Ordinal));
        File.WriteAllBytes(WidgetPath, edited);

        var refusal = Assert.Throws<UnverifiableWorkingTreeException>(() => MutationSentinel.Recover(Root, ".tia"));

        Assert.Contains("git checkout HEAD -- src/Widget.cs", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(SentinelPath, refusal.Message, StringComparison.Ordinal);

        // Nothing touched: not the edit, and not the record a person needs to finish by hand.
        Assert.Equal(edited, File.ReadAllBytes(WidgetPath));
        Assert.True(File.Exists(SentinelPath));
    }

    /// <summary>The command is there to be pasted, so a path with a space has to arrive as one.</summary>
    [Fact]
    public void The_way_out_quotes_a_path_with_a_space()
    {
        var path = Path.Combine(Root, "src", "Old Widget.cs");
        File.WriteAllBytes(path, Bytes(Original));
        MutationSentinel.Write(SentinelPath, Path.Combine("src", "Old Widget.cs"), Bytes(Original), Bytes(Mutated));
        File.WriteAllBytes(path, Bytes(Original + "// edited\r\n"));

        var refusal = Assert.Throws<UnverifiableWorkingTreeException>(() => MutationSentinel.Recover(Root, ".tia"));

        Assert.Contains("git checkout HEAD -- \"src/Old Widget.cs\"", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_that_cannot_be_read_is_refused_rather_than_trusted()
    {
        KillAfterMutating();
        File.WriteAllText(SentinelPath, "{ \"relativePath\": \"src/Widget.cs\", \"orig");

        var refusal = Assert.Throws<UnverifiableWorkingTreeException>(() => MutationSentinel.Recover(Root, ".tia"));

        Assert.Contains(SentinelPath, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(Bytes(Mutated), File.ReadAllBytes(WidgetPath));
    }

    /// <summary>
    /// A record is just a file, and a commit can carry one in even though <c>.tia/</c> is ignored.
    /// Recovering writes the record's bytes to the path it names, so a forged record must not be
    /// able to name a path the harness would never have mutated: one outside the repository, one
    /// inside <c>.git</c>, or one that does not exist yet. Each is forged to match exactly what is
    /// on disk, which is the case a weaker check would act on.
    /// </summary>
    [Theory]
    [InlineData("outside")]
    [InlineData("git")]
    [InlineData("absent")]
    public void A_forged_record_naming_a_path_the_harness_never_mutates_is_refused(string target)
    {
        var payload = Bytes("payload");
        var outside = Path.Combine(Path.GetDirectoryName(Root)!, $"{_directory.Name}-victim.txt");
        var (relativePath, file) = target switch
        {
            "outside" => ($"../{Path.GetFileName(outside)}", outside),
            "git" => (".git/config", Path.Combine(Root, ".git", "config")),
            _ => ("src/New.cs", Path.Combine(Root, "src", "New.cs")),
        };

        if (target == "outside")
        {
            File.WriteAllBytes(outside, Bytes("someone else's file"));
        }

        var before = File.Exists(file) ? File.ReadAllBytes(file) : null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SentinelPath)!);
            File.WriteAllText(SentinelPath, $$"""
                {
                  "relativePath": "{{relativePath}}",
                  "originalSha256": "{{MutationSentinel.Hash(payload)}}",
                  "mutatedSha256": "{{(before is null ? "absent" : MutationSentinel.Hash(before))}}",
                  "original": "{{Convert.ToBase64String(payload)}}"
                }
                """);

            Assert.Throws<UnverifiableWorkingTreeException>(() => MutationSentinel.Recover(Root, ".tia"));

            Assert.Equal(before, File.Exists(file) ? File.ReadAllBytes(file) : null);
            Assert.True(File.Exists(SentinelPath));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void An_absolute_cache_directory_is_honoured()
    {
        var cache = Directory.CreateTempSubdirectory("tia-sentinel-cache-");

        try
        {
            var location = MutationSentinel.Location(Root, cache.FullName);
            Assert.Equal(Path.Combine(cache.FullName, MutationSentinel.FileName), location);

            MutationSentinel.Write(location, Path.Combine("src", "Widget.cs"), Bytes(Original), Bytes(Mutated));
            File.WriteAllBytes(WidgetPath, Bytes(Mutated));

            Assert.NotNull(MutationSentinel.Recover(Root, cache.FullName));
            Assert.Equal(Bytes(Original), File.ReadAllBytes(WidgetPath));
            Assert.False(File.Exists(location));
        }
        finally
        {
            cache.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The wiring inside a real sample, not a reconstruction of it. The harness logs the mutation
    /// right after writing it, which is the moment a kill would leave behind: the record has to be
    /// on disk already. With no solution here the analysis fails inside the sample, and the file
    /// has to come back and the record has to go, as after any sample.
    /// </summary>
    [Fact]
    public async Task A_sample_records_its_mutation_before_writing_it_and_drops_the_record_after_restoring()
    {
        var seenWhileMutated = new List<(bool RecordExists, bool FileMutated)>();
        var harness = new MutationHarness(
            new AnalysisOptions { RepositoryRoot = Root, BaseRef = "HEAD" },
            log: message =>
            {
                if (message.StartsWith("[1]", StringComparison.Ordinal))
                {
                    seenWhileMutated.Add((
                        File.Exists(SentinelPath),
                        !File.ReadAllBytes(WidgetPath).AsSpan().SequenceEqual(Bytes(Original))));
                }
            });

        try
        {
            await RunOneSampleAsync(harness);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SetupException)
        {
            // Whether a failed analysis surfaces as a skipped sample or as one of these does not
            // matter here; the restore runs either way. With no solution in this repository the
            // analysis refuses as a setup error (#50).
        }

        Assert.Equal([(true, true)], seenWhileMutated);
        Assert.Equal(Bytes(Original), File.ReadAllBytes(WidgetPath));
        Assert.False(File.Exists(SentinelPath));
    }

    /// <summary>
    /// A record that cannot be written means no mutation: a mutation without its record is the one
    /// state the next run could not undo.
    /// </summary>
    [Fact]
    public async Task A_sample_whose_record_cannot_be_written_mutates_nothing()
    {
        // A directory where the record would go, so the move onto it fails.
        Directory.CreateDirectory(SentinelPath);
        var logged = false;
        var harness = new MutationHarness(new AnalysisOptions { RepositoryRoot = Root, BaseRef = "HEAD" }, log: _ => logged = true);

        // Windows reports the move onto a directory as access denied, other systems as an IOException.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunOneSampleAsync(harness));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());

        Assert.False(logged);
        Assert.Equal(Bytes(Original), File.ReadAllBytes(WidgetPath));
    }

    // Site 0 of Widget.cs is its one operator, `a + b`, so the sample always mutates.
    private Task<MutationSample?> RunOneSampleAsync(MutationHarness harness) =>
        harness.RunSampleAsync(
            index: 1,
            site: new MutationSite(WidgetPath, 0),
            engine: new MutationEngine(),
            analysisOptions: new AnalysisOptions { RepositoryRoot = Root, BaseRef = "HEAD", FallbackFullOnError = false },
            testProjects: [],
            projectGranularity: false,
            budget: TimeSpan.FromMinutes(1),
            heapLimit: 0,
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// The clean-tree check used to return early when <c>git status</c> failed, so the one guard
    /// against mutating a tree the harness cannot account for let it through whenever git could
    /// not say what the tree held.
    /// </summary>
    [Fact]
    public void A_git_status_that_fails_is_a_refusal_not_a_pass()
    {
        MutationHarness.RequireCleanWorkingTree(Root);

        // A corrupt index: git still finds the repository, and cannot read its state.
        File.WriteAllText(Path.Combine(Root, ".git", "index"), "not an index");

        var refusal = Assert.Throws<UnverifiableWorkingTreeException>(() => MutationHarness.RequireCleanWorkingTree(Root));
        Assert.Contains("git status", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A custom <c>--cache-dir</c> that git does not ignore holds files this harness wrote itself,
    /// and used to make every later run refuse a tree it had dirtied (#44). Anything else untracked
    /// still counts.
    /// </summary>
    [Fact]
    public void Files_in_the_cache_directory_do_not_make_the_tree_dirty()
    {
        Directory.CreateDirectory(Path.Combine(Root, "build-cache"));
        File.WriteAllText(Path.Combine(Root, "build-cache", "runs.jsonl"), "{}");

        MutationHarness.RequireCleanWorkingTree(Root, "build-cache");
        Assert.Throws<InvalidOperationException>(() => MutationHarness.RequireCleanWorkingTree(Root));

        File.WriteAllText(Path.Combine(Root, "src", "New.cs"), "class New;");
        Assert.Throws<InvalidOperationException>(() => MutationHarness.RequireCleanWorkingTree(Root, "build-cache"));
    }

    /// <summary>
    /// Both refusals reach the command line as 2, not 1: nothing is wrong with the selection or
    /// the repository, the harness simply does not know what state the tree is in. Both are
    /// raised before the solution is looked for, so this needs no fixture.
    /// </summary>
    [Theory]
    [InlineData("edited")]
    [InlineData("git-status")]
    public async Task Verify_reports_a_tree_it_cannot_vouch_for_as_inconclusive(string cause)
    {
        if (cause == "edited")
        {
            KillAfterMutating();
            File.WriteAllBytes(WidgetPath, Bytes(Original + "// edited\r\n"));
        }
        else
        {
            File.WriteAllText(Path.Combine(Root, ".git", "index"), "not an index");
        }

        var exitCode = await Cli.InvokeAsync("verify", "--path", Root, "--mutate", "1");

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    public void Dispose()
    {
        // git writes its objects read-only, and Directory.Delete will not remove those on Windows.
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        _directory.Delete(recursive: true);
    }
}
