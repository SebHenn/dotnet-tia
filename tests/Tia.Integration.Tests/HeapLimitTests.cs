using Tia.Core.Infrastructure;
using Tia.Frameworks;
using Tia.Workspace.Harness;

namespace Tia.Integration.Tests;

/// <summary>
/// The per-sample heap limit (issue #28): the memory twin of the per-sample timeout.
/// </summary>
/// <remarks>
/// A mutation can make a loop allocate without end as easily as it can make one never terminate,
/// and the timeout only catches the second. On NodaTime, <c>Instant &lt;=</c> mutated to <c>&lt;</c>
/// took a test host from 0.6 GB to 15 GB in three minutes and the machine running the harness
/// went down before the timeout could fire.
/// </remarks>
[Collection(nameof(FixtureCollection))]
public sealed class HeapLimitTests(XunitFixtureRepository repository) : IDisposable
{
    private const long OneGiB = 1024L * 1024 * 1024;

    public void Dispose() => repository.Revert();

    [Fact]
    public void The_limit_is_a_quarter_of_the_machine()
    {
        Assert.Equal(4 * OneGiB, MutationHarness.HeapLimit(16 * OneGiB));
    }

    [Fact]
    public void A_small_machine_still_gets_enough_for_an_ordinary_suite()
    {
        Assert.Equal(2 * OneGiB, MutationHarness.HeapLimit(4 * OneGiB));
    }

    /// <summary>
    /// The runtime parses GC settings as hex. Written in decimal, 4 GiB reads as about 17 TiB and
    /// the variable is set, visible, and limits nothing.
    /// </summary>
    [Fact]
    public void The_limit_is_written_the_way_the_runtime_reads_it()
    {
        var environment = SuiteRunner.HeapLimitEnvironment(4 * OneGiB, _ => null);

        Assert.NotNull(environment);
        Assert.Equal("0x100000000", environment[SuiteRunner.HeapLimitVariable]);
    }

    [Fact]
    public void A_limit_the_caller_already_exported_is_left_alone()
    {
        Assert.Null(SuiteRunner.HeapLimitEnvironment(4 * OneGiB, _ => "0x40000000"));
    }

    [Fact]
    public void No_limit_sets_nothing()
    {
        Assert.Null(SuiteRunner.HeapLimitEnvironment(null, _ => null));
    }

    [Theory]
    [InlineData("  But was:  <System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.", true)]
    [InlineData("Unhandled exception. System.OutOfMemoryException: Insufficient memory to continue the execution of the program.", true)]
    [InlineData("failed Fixtures.Tests.CalculatorTests.Adds (12ms)\n  Assert.Equal() Failure: Values differ", false)]
    public void A_run_that_hit_the_limit_is_recognised_by_its_output(string output, bool expected)
    {
        Assert.Equal(expected, SuiteRunner.RanOutOfMemory(output));
    }

    [Fact]
    public void A_sample_that_ran_out_of_memory_is_neither_usable_nor_a_miss()
    {
        var result = new MutationHarnessResult(
        [
            new MutationSample { Index = 1, Outcome = SampleOutcome.Clean, FailingTests = 2 },
            new MutationSample { Index = 2, Outcome = SampleOutcome.OutOfMemory, SkipReason = "App.Tests reached the heap limit" },
        ]);

        Assert.Equal(1, result.Usable);
        Assert.Equal(1, result.OutOfMemory);
        Assert.Equal(0, result.Misses);
        Assert.Empty(result.Unobserved);
        Assert.True(result.Passed);
    }

    [Fact]
    public void A_run_of_nothing_but_memory_exhaustion_does_not_pass()
    {
        var result = new MutationHarnessResult(
            [new MutationSample { Index = 1, Outcome = SampleOutcome.OutOfMemory, SkipReason = "App.Tests reached the heap limit" }]);

        Assert.Equal(0, result.Usable);
        Assert.False(result.Passed);
        Assert.False(result.FoundNoMiss);
    }

    /// <summary>
    /// End to end: a real test that allocates far past the limit, run the way the harness runs a
    /// mutated suite. The test stops by itself at 3 GiB, so if the limit did not take effect it
    /// passes and this fails, instead of the machine running out of memory.
    /// </summary>
    [Fact]
    public async Task A_suite_that_allocates_past_the_limit_is_reported_as_out_of_memory_and_its_failures_are_not_read()
    {
        repository.Write("Fixtures.Tests/RunawayTests.cs", """
            namespace Fixtures.Tests;

            public class RunawayTests
            {
                [Fact]
                public void Allocates_until_stopped()
                {
                    var chunks = new List<byte[]>();
                    for (var i = 0; i < 3072; i++)
                    {
                        var chunk = new byte[1024 * 1024];
                        chunk[^1] = 1;
                        chunks.Add(chunk);
                    }

                    Assert.Equal(3072, chunks.Count);
                }
            }
            """);

        var report = await repository.AnalyzeAsync();
        var projects = report.Projects.Where(p => p.Name == "Fixtures.Tests").ToList();
        Assert.Single(projects);

        // Built before the limited run, so the compile happens in an unlimited compiler server
        // rather than starting one under a 1 GiB limit that later builds would reuse.
        var build = ProcessRunner.Run(
            "dotnet",
            ["build", repository.PathOf("Fixtures.Tests", "Fixtures.Tests.csproj")],
            repository.Root,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);

        var run = new SuiteRunner(repository.Root).RunAll(
            projects,
            TestCommandBuilder.ModeOf(report),
            timeout: TimeSpan.FromMinutes(5),
            heapLimitBytes: OneGiB,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Fixtures.Tests"], run.OutOfMemory);
        Assert.Contains("Fixtures.Tests", run.Unobserved);
        Assert.Empty(run.TimedOut);
        Assert.Empty(run.Failures);
    }
}
