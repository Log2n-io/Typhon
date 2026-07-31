using JetBrains.Annotations;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Engine.Profiler;

namespace Typhon.Diagnostics;

/// <summary>
/// Registers this assembly's reflection-based profiling providers with the engine.
///
/// <para>Typhon's engine assembly is Native-AOT-clean and therefore does <b>not</b> reference the ETW / EventPipe tracing packages these two features
/// need (#409). Call <see cref="Enable"/> once during application start-up — before <c>TyphonProfiler.Start</c> / <c>ProfilerLauncher</c> run — to make
/// them available:</para>
///
/// <code>
/// Typhon.Diagnostics.TyphonDiagnostics.Enable();   // opt in to ETW thread scheduling + EventPipe CPU sampling
/// </code>
///
/// <para>Without this call the engine still profiles normally; it simply reports that OS thread-scheduling and CPU sampling are unavailable, the same way
/// it already did on a non-Windows host or without the privileges ETW requires. Referencing this assembly is incompatible with Native AOT publishing.</para>
/// </summary>
[PublicAPI]
public static class TyphonDiagnostics
{
    private static int _enabled;

    /// <summary>
    /// Installs the CPU-sampler, <c>.nettrace</c> parser and ETW scheduling-pump providers into the engine's diagnostics hooks. Idempotent and
    /// thread-safe; subsequent calls are no-ops.
    /// </summary>
    public static void Enable()
    {
        if (Interlocked.Exchange(ref _enabled, 1) != 0)
        {
            return;
        }

        TyphonDiagnosticsHooks.CpuSamplerFactory = static () => new CpuSamplerSession();
        TyphonDiagnosticsHooks.SchedulingPumpFactory = static () => new EtwSchedulingPump();
        DiagnosticsProviderHooks.CpuSampleParse = static path => CpuSampleParser.Parse(path);
    }
}
