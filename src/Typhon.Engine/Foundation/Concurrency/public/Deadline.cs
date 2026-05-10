// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// A monotonic absolute deadline for timeout enforcement.
/// Convert a relative <see cref="TimeSpan"/> to <see cref="Deadline"/> once at the operation entry point,
/// then share the deadline through all nested calls — eliminating timeout accumulation.
/// </summary>
/// <remarks>
/// <para><c>default(Deadline)</c> is <b>already expired</b> (fail-safe). Use <see cref="Infinite"/> for unbounded waits.</para>
/// <para>All time conversions use pure integer arithmetic via the precomputed <see cref="TickRatio"/> constant.
/// No floating-point operations exist anywhere in this type.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public readonly struct Deadline : IEquatable<Deadline>
{
    // ═══════════════════════════════════════════════════════════════════════
    // Tick Conversion Constants (computed once at startup)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stopwatch ticks per TimeSpan tick. Always integral on supported platforms.
    /// <list type="bullet">
    ///   <item>Windows x64: 1 (both are 10 MHz = 100ns resolution)</item>
    ///   <item>Linux: 100 (Stopwatch = 1 GHz, TimeSpan = 10 MHz)</item>
    /// </list>
    /// </summary>
    internal static readonly long TickRatio = Stopwatch.Frequency / TimeSpan.TicksPerSecond;

    static Deadline()
    {
        if (Stopwatch.Frequency % TimeSpan.TicksPerSecond != 0)
        {
            throw new PlatformNotSupportedException(
                $"Stopwatch.Frequency ({Stopwatch.Frequency}) must be an integer multiple " +
                $"of TimeSpan.TicksPerSecond ({TimeSpan.TicksPerSecond}). " +
                $"This platform is not supported.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Instance State
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Absolute monotonic ticks from <see cref="Stopwatch.GetTimestamp"/>.
    /// 0 = already expired (fail-safe default).
    /// <see cref="long.MaxValue"/> = infinite (never expires).
    /// </summary>
    private readonly long _ticks;

    private Deadline(long ticks) => _ticks = ticks;

    /// <summary>Raw monotonic ticks for internal use (priority queue ordering, diagnostics).</summary>
    internal long Ticks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _ticks;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sentinel Values
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>No timeout — never expires.</summary>
    public static readonly Deadline Infinite = new(long.MaxValue);

    /// <summary>Already expired — immediate failure. Equivalent to <c>default(Deadline)</c>.</summary>
    public static readonly Deadline Zero;

    // ═══════════════════════════════════════════════════════════════════════
    // Factory Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Convert a relative timeout to an absolute monotonic deadline.
    /// Call this <b>once</b> at the operation entry point, then share the deadline
    /// through all nested calls.
    /// </summary>
    /// <param name="timeout">
    /// Relative duration. Use <see cref="Timeout.InfiniteTimeSpan"/> for no timeout.
    /// <see cref="TimeSpan.Zero"/> or negative values produce an already-expired deadline.
    /// </param>
    /// <returns>An absolute <see cref="Deadline"/> based on monotonic time.</returns>
    public static Deadline FromTimeout(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return Infinite;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return Zero;
        }

        var ticks = Stopwatch.GetTimestamp() + timeout.Ticks * TickRatio;
        return new Deadline(ticks);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True if the deadline has passed.
    /// Always false for <see cref="Infinite"/>. Always true for <see cref="Zero"/>/<c>default</c>.
    /// Each call reads <see cref="Stopwatch.GetTimestamp"/> — not cached.
    /// </summary>
    public bool IsExpired
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsInfinite && Stopwatch.GetTimestamp() >= _ticks;
    }

    /// <summary>True if this deadline never expires (<c>_ticks == long.MaxValue</c>).</summary>
    public bool IsInfinite
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _ticks == long.MaxValue;
    }

    /// <summary>
    /// Time remaining until this deadline expires.
    /// Returns <see cref="TimeSpan.Zero"/> if already expired.
    /// Returns <see cref="Timeout.InfiniteTimeSpan"/> if infinite.
    /// </summary>
    public TimeSpan Remaining
    {
        get
        {
            if (IsInfinite)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var remaining = _ticks - Stopwatch.GetTimestamp();
            return (remaining <= 0) ? TimeSpan.Zero : new TimeSpan(remaining / TickRatio);
        }
    }

    /// <summary>
    /// Remaining time in milliseconds.
    /// Returns 0 if expired, -1 if infinite.
    /// Suitable for .NET wait APIs (<see cref="Monitor.Wait(object,int)"/>, <see cref="WaitHandle.WaitOne(int)"/>).
    /// </summary>
    public int RemainingMilliseconds
    {
        get
        {
            if (IsInfinite)
            {
                return -1;
            }

            var remainingStopwatchTicks = _ticks - Stopwatch.GetTimestamp();
            if (remainingStopwatchTicks <= 0)
            {
                return 0;
            }

            // Convert Stopwatch ticks → TimeSpan ticks → milliseconds (all integer)
            return (int)(remainingStopwatchTicks / TickRatio / TimeSpan.TicksPerMillisecond);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Utility Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the tighter (earlier) of two deadlines.
    /// Useful when an inner operation has its own timeout that must also
    /// respect the outer deadline.
    /// </summary>
    public static Deadline Min(Deadline a, Deadline b)
        => a._ticks < b._ticks ? a : b;

    /// <summary>
    /// Bridge a deadline to a .NET <see cref="CancellationToken"/>.
    /// Creates a <see cref="CancellationTokenSource"/> that cancels when the deadline expires.
    /// </summary>
    /// <remarks>
    /// <b>WARNING:</b> This allocates a <see cref="CancellationTokenSource"/> with a timer.
    /// Use sparingly — only when interacting with .NET APIs that require
    /// a <see cref="CancellationToken"/> (e.g., HttpClient, Stream.ReadAsync).
    /// For Typhon's own lock primitives, use WaitContext directly.
    /// </remarks>
    public CancellationToken ToCancellationToken()
    {
        if (IsInfinite)
        {
            return CancellationToken.None;
        }

        if (IsExpired)
        {
            return new CancellationToken(true);
        }

        var remaining = Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            return new CancellationToken(true);
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(remaining);
        return cts.Token;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Equality
    // ═══════════════════════════════════════════════════════════════════════

    public bool Equals(Deadline other) => _ticks == other._ticks;
    public override bool Equals(object obj) => obj is Deadline other && Equals(other);
    public override int GetHashCode() => _ticks.GetHashCode();

    public static bool operator ==(Deadline left, Deadline right) => left._ticks == right._ticks;
    public static bool operator !=(Deadline left, Deadline right) => left._ticks != right._ticks;

    /// <inheritdoc />
    public override string ToString()
    {
        if (_ticks == 0)
        {
            return "Deadline(Expired)";
        }

        if (_ticks == long.MaxValue)
        {
            return "Deadline(Infinite)";
        }

        return $"Deadline(Remaining={Remaining})";
    }
}
