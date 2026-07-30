using System.Diagnostics;
using Tia.Core.Reporting;

namespace Tia.Workspace;

/// <summary>
/// Accumulates how long each phase of an analysis took.
/// </summary>
/// <remarks>
/// This tool competes with the test suite it is trying to avoid running, so its own cost is a
/// product concern rather than a profiling detail - and the phase that dominates has not been the
/// one anybody guessed.
/// </remarks>
public sealed class PhaseClock
{
    private readonly Dictionary<string, double> _seconds = new(StringComparer.Ordinal);

    public T Time<T>(string phase, Func<T> work)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return work();
        }
        finally
        {
            Record(phase, started);
        }
    }

    public void Record(string phase, long startedTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds;
        lock (_seconds)
        {
            _seconds[phase] = _seconds.GetValueOrDefault(phase) + elapsed;
        }
    }

    public PhaseTimings Snapshot()
    {
        lock (_seconds)
        {
            return new PhaseTimings
            {
                WorkspaceLoadSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.WorkspaceLoadSeconds)),
                SolutionOpenSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.SolutionOpenSeconds)),
                CompilationSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.CompilationSeconds)),
                GeneratorProbeSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.GeneratorProbeSeconds)),
                CompileCheckSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.CompileCheckSeconds)),
                FingerprintSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.FingerprintSeconds)),
                GraphSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.GraphSeconds)),
                DiffSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.DiffSeconds)),
                SelectionSeconds = _seconds.GetValueOrDefault(nameof(PhaseTimings.SelectionSeconds)),
            };
        }
    }
}
