using System;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// The engine's side of request correlation inside the tick (#1517): which system is running on this thread, and the per-command scoping the command
/// enumerator applies. Folded away while <see cref="TelemetryConfig.ProfilerCorrelationActive"/> is off.
/// </summary>
internal static class CorrelationContext
{
    /// <summary>The running system's index + 1, so zero means none.</summary>
    [ThreadStatic] private static int _systemPlusOne;

    /// <summary>Called by the scheduler as a system body starts on this thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnterSystem(int sysIdx)
    {
        if (TelemetryConfig.ProfilerCorrelationActive)
        {
            _systemPlusOne = sysIdx + 1;
        }
    }

    /// <summary>
    /// Called by the scheduler as a system body ends. Also clears the causal scope: a system that broke out of its command loop left the last command's
    /// id in scope, and the next system on this thread must not inherit it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LeaveSystem()
    {
        if (TelemetryConfig.ProfilerCorrelationActive)
        {
            _systemPlusOne = 0;
            CausalScope.Clear();
        }
    }

    /// <summary>
    /// The command enumerator stepped onto a command: put its causal id in scope and record that this thread's system consumed it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnCommand(uint session, ushort seq, long drainTick)
    {
        if (TelemetryConfig.ProfilerCorrelationActive)
        {
            OnCommandCore(session, seq, drainTick);
        }
    }

    /// <summary>The command enumerator reached the end of its batch: put back the id the loop started under (none, or an enclosing scope's).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnBatchEnd(CausalId outer)
    {
        if (TelemetryConfig.ProfilerCorrelationActive)
        {
            CausalScope.Set(outer.Hi, outer.Lo);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OnCommandCore(uint session, ushort seq, long drainTick)
    {
        var (hi, lo) = CorrelationInstantCodec.CommandCausalId(session, unchecked((uint)drainTick), seq);
        CausalScope.Set(hi, lo);
        TyphonEvent.EmitCommandConsumed(session, seq, _systemPlusOne == 0 ? ushort.MaxValue : (ushort)(_systemPlusOne - 1));
    }
}
