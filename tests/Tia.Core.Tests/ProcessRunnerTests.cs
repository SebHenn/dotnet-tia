using System.Diagnostics;
using Tia.Core.Infrastructure;

namespace Tia.Core.Tests;

/// <summary>
/// The bounded wait on a child process.
/// </summary>
/// <remarks>
/// Nothing here waited on anything. A <c>git</c> blocked on a credential helper prompting at a
/// terminal nobody is watching, or on an <c>index.lock</c> left behind by a crashed editor, hung
/// the CLI forever with no output - in CI, a job that burns its whole time budget and reports
/// nothing. The commands the engine issues answer in milliseconds, so anything else is a reason to
/// stop rather than to keep waiting.
/// </remarks>
public sealed class ProcessRunnerTests
{
    [Fact]
    public void A_child_that_outstays_its_timeout_is_killed_and_reported()
    {
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<TimeoutException>(() => ProcessRunner.Run(
            Sleep.FileName,
            Sleep.Arguments(seconds: 60),
            Directory.GetCurrentDirectory(),
            timeout: TimeSpan.FromSeconds(2),
            cancellationToken: TestContext.Current.CancellationToken));

        // The point is that it returns; the message is what makes the return diagnosable.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"waited {stopwatch.Elapsed}");
        Assert.Contains("did not exit within", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Sleep.FileName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_child_that_finishes_inside_its_timeout_is_left_alone()
    {
        var result = ProcessRunner.Run(
            Sleep.FileName,
            Sleep.Arguments(seconds: 0),
            Directory.GetCurrentDirectory(),
            timeout: TimeSpan.FromMinutes(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.StandardError);
    }

    [Fact]
    public void Cancellation_is_reported_as_cancellation_and_not_as_a_timeout()
    {
        // The two paths both kill the child, and telling them apart matters: a timeout is a fault
        // to report, Ctrl-C is not.
        using var source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromMilliseconds(200));

        Assert.Throws<TaskCanceledException>(() => ProcessRunner.Run(
            Sleep.FileName,
            Sleep.Arguments(seconds: 60),
            Directory.GetCurrentDirectory(),
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: source.Token));
    }

    [Fact]
    public void An_environment_variable_reaches_the_child()
    {
        // GitClient sets GIT_TERMINAL_PROMPT=0 this way, which is what turns the commonest cause
        // of a hang into an immediate failure rather than a two-minute wait.
        var result = ProcessRunner.Run(
            Sleep.ShellFileName,
            Sleep.EchoVariable("TIA_PROBE"),
            Directory.GetCurrentDirectory(),
            environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["TIA_PROBE"] = "visible" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("visible", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>A process that sleeps, and a shell that echoes, on whichever OS is running.</summary>
    private static class Sleep
    {
        public static string FileName => OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sleep";

        public static string ShellFileName => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

        public static string[] Arguments(int seconds) => OperatingSystem.IsWindows()
            // cmd's `timeout` needs a console it does not have with the streams redirected.
            ? ["-NoProfile", "-Command", $"Start-Sleep -Seconds {seconds}"]
            : [seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];

        public static string[] EchoVariable(string name) => OperatingSystem.IsWindows()
            ? ["/c", $"echo %{name}%"]
            : ["-c", $"echo ${name}"];
    }
}
