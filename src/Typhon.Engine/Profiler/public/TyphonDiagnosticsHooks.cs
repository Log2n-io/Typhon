using JetBrains.Annotations;
using System;

namespace Typhon.Engine.Profiler;

/// <summary>
/// An OS-level thread-scheduling pump: consumes kernel context-switch events and feeds the profiler one record per ON-CPU slice for Typhon-registered
/// threads. Implemented out-of-assembly (see <c>Typhon.Diagnostics</c>) because every viable implementation needs a reflection-heavy tracing package.
/// </summary>
[PublicAPI]
public interface ISchedulingPump : IDisposable
{
    /// <summary>Opens the kernel tracing session and starts pumping. Best-effort: a failure must be reported to stderr, never thrown into the host.</summary>
    /// <returns><see langword="true"/> when the session started; <see langword="false"/> when the platform, privileges or another tool prevented it.</returns>
    bool Start();
}

/// <summary>
/// An in-process CPU sampling session that captures a <c>.nettrace</c> for later symbol resolution. Implemented out-of-assembly (see
/// <c>Typhon.Diagnostics</c>) because it requires the EventPipe diagnostics client.
/// </summary>
[PublicAPI]
public interface ICpuSamplerSession : IDisposable
{
    /// <summary>QPC timestamp captured when sampling began; the profiler uses it to align samples with its own span timeline.</summary>
    long SamplingSessionStartQpc { get; }

    /// <summary>Path of the transient <c>.nettrace</c> capture. Readable after <see cref="IDisposable.Dispose"/> finalizes the session.</summary>
    string NetTracePath { get; }

    /// <summary>Starts the sampling session, writing its capture next to <paramref name="traceFilePath"/>.</summary>
    /// <param name="traceFilePath">The Typhon trace file this session accompanies.</param>
    void Start(string traceFilePath);
}

/// <summary>
/// Registration point for the optional, reflection-heavy diagnostics providers that the engine deliberately does <b>not</b> depend on.
///
/// <para><b>Why this seam exists (#409).</b> ETW kernel tracing (<c>Microsoft.Diagnostics.Tracing.TraceEvent</c>) and EventPipe CPU sampling
/// (<c>Microsoft.Diagnostics.NETCore.Client</c>) are both fundamentally reflection-driven and cannot be made Native-AOT-safe. Referencing them from the
/// engine would make the engine assembly non-AOT for two opt-in profiling features that a shipped application almost never enables. They now live in the
/// separate <c>Typhon.Diagnostics</c> assembly; an application that wants them adds that reference and calls <c>TyphonDiagnostics.Enable()</c>. An
/// application that does not — including every Native AOT build — never pulls those packages into its dependency graph at all.</para>
///
/// <para>Both hooks are null by default. The engine treats a null hook as "feature unavailable" and degrades exactly as it already did when the OS or
/// privileges denied the capability: a stderr note and profiling continues without that data source.</para>
///
/// <para><b>Cost:</b> nil. Both hooks are read once per profiling-session start — never on an emit path, never per tick.</para>
/// </summary>
[PublicAPI]
public static class TyphonDiagnosticsHooks
{
    /// <summary>Factory for the OS thread-scheduling pump, or <see langword="null"/> when no provider is registered.</summary>
    public static Func<ISchedulingPump> SchedulingPumpFactory { get; set; }

    /// <summary>Factory for the CPU sampling session, or <see langword="null"/> when no provider is registered.</summary>
    public static Func<ICpuSamplerSession> CpuSamplerFactory { get; set; }

    /// <summary>True when a CPU-sampling provider is registered; hosts can use this to explain why sampling was skipped.</summary>
    public static bool IsCpuSamplingAvailable => CpuSamplerFactory != null;

    /// <summary>True when a thread-scheduling provider is registered.</summary>
    public static bool IsSchedulingPumpAvailable => SchedulingPumpFactory != null;
}
