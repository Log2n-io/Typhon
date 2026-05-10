// unset

using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// A 16-byte immutable value type that bundles a monotonic deadline with cooperative cancellation,
/// passed by reference to all blocking synchronization primitives.
/// </summary>
/// <remarks>
/// <para><c>default(WaitContext)</c> is <b>already expired</b> (fail-safe) — the deadline is expired and
/// any lock primitive will return <c>false</c> immediately.</para>
/// <para>For unbounded wait (infinite deadline, no cancellation), use <c>ref Unsafe.NullRef&lt;WaitContext&gt;()</c>
/// instead of constructing a WaitContext.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public readonly struct WaitContext
{
    // ═══════════════════════════════════════════════════════════════════════
    // Fields (immutable after construction)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Absolute monotonic deadline via <see cref="Stopwatch.GetTimestamp"/>.
    /// <c>default</c> = <see cref="Deadline.Zero"/> (already expired).
    /// <see cref="Deadline.Infinite"/> = no timeout.
    /// </summary>
    public readonly Deadline Deadline;

    /// <summary>
    /// Cooperative cancellation token from session or application layer.
    /// <c>default</c> = <see cref="CancellationToken.None"/> (never cancelled).
    /// </summary>
    public readonly CancellationToken Token;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor (private — use factory methods)
    // ═══════════════════════════════════════════════════════════════════════

    internal WaitContext(Deadline deadline, CancellationToken token)
    {
        Deadline = deadline;
        Token = token;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // NullRef for Infinite Wait
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a null reference to WaitContext for infinite wait scenarios.
    /// Use this instead of <c>Unsafe.NullRef&lt;WaitContext&gt;()</c> for cleaner call sites.
    /// </summary>
    /// <example>
    /// <code>
    /// // Instead of this (cluttered):
    /// lock.EnterExclusiveAccess(ref Unsafe.NullRef&lt;WaitContext&gt;());
    ///
    /// // Use this (clean):
    /// lock.EnterExclusiveAccess(ref WaitContext.Null);
    /// </code>
    /// </example>
    public static ref WaitContext Null
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Unsafe.NullRef<WaitContext>();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Factory Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Wrap an existing <see cref="Deadline"/> (no cancellation).</summary>
    /// <param name="deadline">The deadline to use.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromDeadline(Deadline deadline)
        => new(deadline, CancellationToken.None);

    /// <summary>Create from relative timeout (no cancellation).</summary>
    /// <param name="timeout">
    /// Relative duration. Use <see cref="Timeout.InfiniteTimeSpan"/> for no timeout.
    /// <see cref="TimeSpan.Zero"/> or negative values produce an already-expired context.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromTimeout(TimeSpan timeout)
        => new(Deadline.FromTimeout(timeout), CancellationToken.None);

    /// <summary>Create from relative timeout + cancellation token.</summary>
    /// <param name="timeout">
    /// Relative duration. Use <see cref="Timeout.InfiniteTimeSpan"/> for no timeout.
    /// </param>
    /// <param name="token">Cancellation token from session or application layer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromTimeout(TimeSpan timeout, CancellationToken token)
        => new(Deadline.FromTimeout(timeout), token);

    /// <summary>Create with cancellation only (infinite deadline).</summary>
    /// <param name="token">Cancellation token from session or application layer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromToken(CancellationToken token)
        => new(Deadline.Infinite, token);

    /// <summary>Create from an existing <see cref="UnitOfWorkContext"/> (reads embedded WaitContext).</summary>
    /// <param name="ctx">The execution context containing the embedded WaitContext.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WaitContext FromUnitOfWorkContext(ref UnitOfWorkContext ctx)
        => ctx.WaitContext;

    // ═══════════════════════════════════════════════════════════════════════
    // Termination Check
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True if the wait should stop: deadline expired OR cancellation requested.
    /// Called once per spin iteration in lock primitives.
    /// </summary>
    /// <remarks>
    /// <para>Cost per call: ~10-25ns for deadline check (one <c>Stopwatch.GetTimestamp()</c>)
    /// plus ~0-1 volatile read for cancellation (short-circuits for <see cref="CancellationToken.None"/>).</para>
    /// <para>Lock primitives check <c>Unsafe.IsNullRef(ref ctx)</c> once at entry and skip
    /// this property entirely when NullRef is passed — the fastest possible spin path.</para>
    /// </remarks>
    public bool ShouldStop
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Deadline.IsExpired || Token.IsCancellationRequested;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Diagnostic Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True if this WaitContext has an infinite deadline and no cancellation token.
    /// Such a context will never trigger <see cref="ShouldStop"/> on its own.
    /// </summary>
    public bool IsUnbounded
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Deadline.IsInfinite && !Token.CanBeCanceled;
    }

    /// <summary>
    /// Remaining time until deadline, or <see cref="Timeout.InfiniteTimeSpan"/> if infinite.
    /// Delegates to <see cref="Deadline.Remaining"/>.
    /// </summary>
    public TimeSpan Remaining
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Deadline.Remaining;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var tokenState = Token.CanBeCanceled ? "active" : "none";
        return $"WaitContext(Deadline={Deadline}, Token={tokenState}, ShouldStop={ShouldStop})";
    }
}
