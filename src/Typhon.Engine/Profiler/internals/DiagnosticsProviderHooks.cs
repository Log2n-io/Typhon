using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Engine-internal half of the optional-diagnostics seam (see <see cref="Typhon.Engine.Profiler.TyphonDiagnosticsHooks"/> for the public half and the
/// full rationale). Lives here rather than on the public hook class because <see cref="ParsedCpuSamples"/> is an internal type — the trace-trailer format
/// is not public API. Set by <c>Typhon.Diagnostics</c>, which is an <c>InternalsVisibleTo</c> friend assembly.
/// </summary>
internal static class DiagnosticsProviderHooks
{
    /// <summary>
    /// Parses a <c>.nettrace</c> CPU-sample capture into resolved, interned samples, or <see langword="null"/> when no provider is registered. Reached
    /// once per profiling-session stop.
    /// </summary>
    internal static Func<string, ParsedCpuSamples> CpuSampleParse { get; set; }
}
