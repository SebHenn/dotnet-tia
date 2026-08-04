using System.Diagnostics;
using System.Text;

namespace Tia.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public static class ProcessRunner
{
    /// <summary>
    /// Runs a child process and captures both streams. Arguments are passed through
    /// <see cref="ProcessStartInfo.ArgumentList"/> so no shell quoting rules apply.
    /// </summary>
    /// <param name="timeout">
    /// Bounded wait, or null to wait indefinitely. Null is the default because the longest-running
    /// caller here is a whole test suite under the mutation harness, and capping that would turn a
    /// slow repository into a failure. Callers whose child process has a knowable duration - git,
    /// which answers in milliseconds unless it is blocked on a prompt or an index lock - pass one.
    /// </param>
    /// <param name="environment">Variables to set on the child, overriding what it would inherit.</param>
    public static ProcessResult Run(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.Append(e.Data).Append('\n');
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.Append(e.Data).Append('\n');
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (timeout is null)
        {
            try
            {
                process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }
        else
        {
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            expiry.CancelAfter(timeout.Value);

            try
            {
                process.WaitForExitAsync(expiry.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException(FormattableString.Invariant(
                    $"{fileName} did not exit within {timeout.Value.TotalSeconds:0}s and was killed. ") +
                    $"Arguments: {string.Join(' ', startInfo.ArgumentList)}");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs a child process, streaming both output streams to callbacks as they arrive. Used for
    /// <c>dotnet test</c>, where hiding output until the run completes would be unhelpful.
    /// </summary>
    public static int RunStreaming(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onError(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return process.ExitCode;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process already exited, or we lack the rights to signal it. Nothing to do.
        }
    }
}
