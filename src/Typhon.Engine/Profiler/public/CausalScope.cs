using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// The causal id in scope on this thread: a 128-bit W3C-compatible trace id that every profiler span begun here carries, so the Workbench can tie
/// engine work back to the HTTP request or client command that caused it (#1517).
/// </summary>
/// <remarks>
/// <para>
/// <b>Takes precedence over <see cref="System.Diagnostics.Activity.Current"/>.</b> A span reads the scoped id first and only falls back to the ambient
/// Activity when none is set. Tick work runs on workers that never read the Activity (it costs too much per span there), so the scoped id is the only
/// way a cause reaches them. HTTP work needs nothing from here: the request's Activity is an <c>AsyncLocal</c> and follows every <c>await</c>, which a
/// thread-static set when the request started would not.
/// </para>
/// <para>
/// <b>Work that crosses threads</b> carries its cause explicitly: <see cref="Capture"/> where the work is queued, <see cref="Enter(CausalId)"/> where
/// it runs. An application that stages writes into the tick does exactly this.
/// </para>
/// <para>
/// Everything here is a no-op while <see cref="TelemetryConfig.ProfilerCorrelationActive"/> is off.
/// </para>
/// </remarks>
[PublicAPI]
public static class CausalScope
{
    [ThreadStatic] private static ulong _hi;
    [ThreadStatic] private static ulong _lo;

    /// <summary>The id in scope on this thread, or <see cref="CausalId.None"/>.</summary>
    public static CausalId Current => new(_hi, _lo);

    /// <summary>The id in scope, or the ambient <see cref="System.Diagnostics.Activity.Current"/>'s trace id, to carry onto another thread.</summary>
    /// <returns><see cref="CausalId.None"/> while correlation is off or nothing is in scope.</returns>
    public static CausalId Capture()
    {
        if (!TelemetryConfig.ProfilerCorrelationActive)
        {
            return CausalId.None;
        }

        if ((_hi | _lo) != 0)
        {
            return new CausalId(_hi, _lo);
        }

        var activity = System.Diagnostics.Activity.Current;
        return activity == null ? CausalId.None : CausalId.FromTraceId(activity.TraceId);
    }

    /// <summary>Puts <paramref name="id"/> in scope until the returned scope is disposed, which restores what was there before.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Scope Enter(CausalId id)
    {
        if (!TelemetryConfig.ProfilerCorrelationActive)
        {
            return default;
        }

        var previous = new CausalId(_hi, _lo);
        _hi = id.Hi;
        _lo = id.Lo;
        return new Scope(previous, true);
    }

    /// <summary>Sets the id in scope with no scope to restore it: for an iterator that moves from one cause to the next.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Set(ulong hi, ulong lo)
    {
        _hi = hi;
        _lo = lo;
    }

    /// <summary>Clears the id in scope. The scheduler calls it after every system, so a cause cannot leak into the next system's spans.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Clear()
    {
        _hi = 0;
        _lo = 0;
    }

    /// <summary>Read by the profiler's span prologue: the scoped id, or zeros.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGet(out ulong hi, out ulong lo)
    {
        hi = _hi;
        lo = _lo;
        return (hi | lo) != 0;
    }

    /// <summary>Restores the previously scoped id when disposed.</summary>
    [PublicAPI]
    public readonly ref struct Scope
    {
        private readonly CausalId _previous;
        private readonly bool _active;

        internal Scope(CausalId previous, bool active)
        {
            _previous = previous;
            _active = active;
        }

        /// <summary>Restores the id that was in scope before.</summary>
        public void Dispose()
        {
            if (_active)
            {
                _hi = _previous.Hi;
                _lo = _previous.Lo;
            }
        }
    }
}

/// <summary>A causal id: the 16 bytes of a W3C trace id, as two halves. Zero means none.</summary>
/// <param name="Hi">The first 8 bytes, read little-endian — the layout the profiler's span trace context uses.</param>
/// <param name="Lo">The last 8 bytes.</param>
[PublicAPI]
public readonly record struct CausalId(ulong Hi, ulong Lo)
{
    /// <summary>No causal id.</summary>
    public static CausalId None => default;

    /// <summary>True when this is <see cref="None"/>.</summary>
    public bool IsNone => (Hi | Lo) == 0;

    /// <summary>The causal id of a W3C trace id, split the way the span prologue splits <see cref="System.Diagnostics.Activity.TraceId"/>.</summary>
    public static CausalId FromTraceId(System.Diagnostics.ActivityTraceId traceId)
    {
        Span<byte> bytes = stackalloc byte[16];
        traceId.CopyTo(bytes);
        // The same native-order reads as TyphonEvent.BeginPrologue's Activity branch, so a scoped id and an ambient one can never split differently.
        return new CausalId(System.Runtime.InteropServices.MemoryMarshal.Read<ulong>(bytes),
            System.Runtime.InteropServices.MemoryMarshal.Read<ulong>(bytes[8..]));
    }
}
