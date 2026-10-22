using System.Text;
using Tia.Cli;
using Tia.Core.Infrastructure;
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
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "tia@example.invalid");
        Git("config", "user.name", "tia tests");
        Git("config", "core.autocrlf", "false");

        Directory.CreateDirectory(Path.Combine(Root, "src"));
        File.WriteAllBytes(WidgetPath, Bytes(Original));
        Git("add", "-A");
        Git("commit", "-m", "widget");
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

        Assert.Contains("git checkout -- src/Widget.cs", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(SentinelPath, refusal.Message, StringComparison.Ordinal);

        // Nothing touched: not the edit, and not the record a person needs to finish by hand.
        Assert.Equal(edited, File.ReadAllBytes(WidgetPath));
        Assert.True(File.Exists(SentinelPath));
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

        var exitCode = await Program.BuildRoot(new CommonOptions())
            .Parse(["verify", "--path", Root, "--mutate", "1"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    private void Git(params string[] args)
    {
        var result = ProcessRunner.Run("git", args, Root, cancellationToken: TestContext.Current.CancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
        }
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
