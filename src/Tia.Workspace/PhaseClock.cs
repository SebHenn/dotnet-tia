using System.Diagnostics;
using Tia.Core.Reporting;

namespace Tia.Workspace;

/// <summary>
/// Accumulates how long each phase of an analysis took.
/// </summary>
/// <remarks>
/// <para>
/// This tool competes with the test suite it is trying to avoid running, so its own cost is a
/// product concern rather than a profiling detail - and the phase that dominates has not been the
/// one anybody guessed.
/// </para>
/// <para>
/// Every recording accumulates, which is right for a phase entered many times and misleading for
/// one entered from several threads at once. Phases inside the parallel rebuild are therefore
/// named <c>…CpuSeconds</c> in <see cref="PhaseTimings"/> and can exceed the wall-clock phase that
/// contains them; see the remarks there. <see cref="Measure"/> exists so a caller inside the
/// parallel region does not have to remember which it is dealing with.
/// </para>
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

    /// <summary>
    /// Times one slice of work and charges it to a CPU-sum phase. Same mechanics as
    /// <see cref="Time{T}"/>; the separate name is the reminder that the result is a sum across
    /// threads rather than a duration.
    /// </summary>
    public T Measure<T>(string cpuPhase, Func<T> work) => Time(cpuPhase, work);

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
                WorkspaceLoadSeconds = Get(nameof(PhaseTimings.WorkspaceLoadSeconds)),
                SolutionOpenSeconds = Get(nameof(PhaseTimings.SolutionOpenSeconds)),
                DiffSeconds = Get(nameof(PhaseTimings.DiffSeconds)),
                FingerprintSeconds = Get(nameof(PhaseTimings.FingerprintSeconds)),
                GraphSeconds = Get(nameof(PhaseTimings.GraphSeconds)),
                SelectionSeconds = Get(nameof(PhaseTimings.SelectionSeconds)),
                CompilationCpuSeconds = Get(nameof(PhaseTimings.CompilationCpuSeconds)),
                GeneratorProbeSeconds = Get(nameof(PhaseTimings.GeneratorProbeSeconds)),
                GraphWalkCpuSeconds = Get(nameof(PhaseTimings.GraphWalkCpuSeconds)),
                ReflectionScanCpuSeconds = Get(nameof(PhaseTimings.ReflectionScanCpuSeconds)),
                TestDiscoveryCpuSeconds = Get(nameof(PhaseTimings.TestDiscoveryCpuSeconds)),
                SurfaceHashCpuSeconds = Get(nameof(PhaseTimings.SurfaceHashCpuSeconds)),
                CompileCheckCpuSeconds = Get(nameof(PhaseTimings.CompileCheckCpuSeconds)),
            };

            double Get(string phase) => _seconds.GetValueOrDefault(phase);
        }
    }
}
