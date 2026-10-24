using Tia.Cli.Commands;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary>
/// <c>run --parallel</c> (#74): projects side by side, with what a sequential run promised kept -
/// the first failure in project order decides the exit code, and <c>--fail-fast</c> stops the rest.
/// </summary>
public sealed class ParallelRunTests
{
    private static ProjectSelection Project(string name) => new()
    {
        Name = name,
        ProjectPath = $"/repo/{name}.csproj",
        Framework = "XUnitV3",
        Runner = "VsTest",
        TotalTests = 1,
        SelectedTests = 1,
        Filtered = false,
    };

    private static readonly IReadOnlyList<ProjectSelection> Three = [Project("A"), Project("B"), Project("C")];

    [Fact]
    public void Projects_run_at_the_same_time_up_to_the_limit()
    {
        var running = 0;
        var peak = 0;

        SelectiveRun.RunSideBySide(Three, new SelectiveRunSettings(MaxParallel: 2), (_, _, _, token) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(200);
            Interlocked.Decrement(ref running);
            return new SelectiveRun.ProjectRun(0, Ran: true, Abandoned: false);
        }, CancellationToken.None);

        Assert.Equal(2, peak);
    }

    [Fact]
    public void Results_come_back_in_project_order_whatever_finished_first()
    {
        // C finishes first and B fails; a sequential run would have reported B's code.
        var results = SelectiveRun.RunSideBySide(Three, new SelectiveRunSettings(MaxParallel: 3), (project, _, _, _) =>
        {
            Thread.Sleep(project.Name == "C" ? 0 : 150);
            return new SelectiveRun.ProjectRun(project.Name == "B" ? 3 : 0, Ran: true, Abandoned: false);
        }, CancellationToken.None);

        Assert.Equal([0, 3, 0], results.Select(r => r.Exit));
    }

    [Fact]
    public void Fail_fast_stops_the_projects_still_running()
    {
        var stopped = 0;

        // A fails only once B and C are both running: failing before they start is the other
        // case, where they are never started rather than stopped, and the scheduler is free to
        // reach either first.
        using var running = new CountdownEvent(2);

        var results = SelectiveRun.RunSideBySide(Three, new SelectiveRunSettings(FailFast: true, MaxParallel: 3), (project, _, _, token) =>
        {
            if (project.Name == "A")
            {
                Assert.True(running.Wait(TimeSpan.FromSeconds(30), CancellationToken.None));
                return new SelectiveRun.ProjectRun(1, Ran: true, Abandoned: true);
            }

            running.Signal();
            try
            {
                Task.Delay(TimeSpan.FromSeconds(30), token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref stopped);
                throw;
            }

            return new SelectiveRun.ProjectRun(0, Ran: true, Abandoned: false);
        }, CancellationToken.None);

        Assert.Equal(2, stopped);
        Assert.Equal(1, results[0].Exit);
        Assert.All(results, r => Assert.True(r.Abandoned));
    }

    [Fact]
    public void Each_projects_output_is_printed_whole()
    {
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            SelectiveRun.RunSideBySide(Three, new SelectiveRunSettings(MaxParallel: 3), (project, _, output, _) =>
            {
                for (var i = 0; i < 20; i++)
                {
                    output.Line($"{project.Name}{i}");
                    Thread.Yield();
                }

                return new SelectiveRun.ProjectRun(0, Ran: true, Abandoned: false);
            }, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }

        // Whatever order the projects finished in, each one's lines are contiguous.
        var lines = captured.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))
            .Select(l => l[0])
            .ToList();

        var runs = lines.Zip(lines.Skip(1)).Count(pair => pair.First != pair.Second) + 1;
        Assert.Equal(3, runs);
        Assert.Equal(60, lines.Count);
    }

    [Theory]
    [InlineData(new[] { "--no-build" }, true)]
    [InlineData(new[] { "--configuration", "Release", "--no-build" }, true)]
    [InlineData(new[] { "--", "--no-build" }, false)]
    [InlineData(new string[0], false)]
    public void Only_a_run_against_built_binaries_may_go_side_by_side(string[] passthrough, bool expected)
    {
        // After `--` the argument belongs to the runner, and dotnet test would still build.
        Assert.Equal(expected, SelectiveRun.BuildsNothing(passthrough));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
