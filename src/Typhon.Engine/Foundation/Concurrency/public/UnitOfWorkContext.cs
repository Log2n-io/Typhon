using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// A 24-byte execution context that flows through all operations inside a Unit of Work (or standalone transaction). Embeds a <see cref="WaitContext"/>
/// (deadline + cancellation) and adds UoW identity and holdoff state. Lock sites can use <c>ref ctx.WaitContext</c> directly — zero construction cost.
/// </summary>
/// <remarks>
/// <para><c>default(UnitOfWorkContext)</c> is <b>already expired</b> (fail-safe) — the embedded deadline is expired and <see cref="ThrowIfCancelled"/>
/// will throw immediately. Use <see cref="None"/> for unbounded operations (infinite deadline, no cancellation).</para>
/// <para>This is a value type passed by <c>ref</c> through synchronous call chains. The <see cref="System.Threading.CancellationTokenSource"/> that generates
/// the token is owned externally — by the future UnitOfWork class (Tier 3) or by <see cref="DeadlineWatchdog"/>.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[NoCopy]
[PublicAPI]
public struct UnitOfWorkContext
{
    // ═══════════════════════════════════════════════════════════════
    // Fields (24 bytes = 3 × 8-byte qwords, naturally aligned)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Embedded wait context carrying deadline + cancellation token. Lock call sites can use <c>ref ctx.WaitContext</c> directly.
    /// Non-readonly to allow <c>ref</c> passing to lock primitives.
    /// </summary>
    public WaitContext WaitContext;                                  // 16 bytes (Deadline 8B + Token 8B)

    /// <summary>UoW identifier for revision stamping and crash recovery. Distinct from <c>EpochManager.GlobalEpoch</c> (EBRM resource lifecycle).</summary>
    public readonly ushort UowId;                                   // 2 bytes

    private readonly ushort _padding;                               // 2 bytes (alignment)

    /// <summary>
    /// Holdoff counter. When &gt; 0, <see cref="ThrowIfCancelled"/> is a no-op. Supports nesting (increment on enter, decrement on exit).
    /// </summary>
    internal int _holdoffCount;                                     // 4 bytes

    // ═══════════════════════════════════════════════════════════════
    // Construction
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Primary constructor — WaitContext + UoW ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UnitOfWorkContext(WaitContext waitContext, ushort uowId = 0)
    {
        WaitContext = waitContext;
        UowId = uowId;
        _padding = 0;
        _holdoffCount = 0;
    }

    /// <summary>Primary constructor — deadline + cancellation token.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UnitOfWorkContext(Deadline deadline, CancellationToken token, ushort uowId = 0) : this(new WaitContext(deadline, token), uowId)
    {
    }

    /// <summary>Create from a relative timeout (no cancellation, no UoW ID).</summary>
    [AllowCopy]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UnitOfWorkContext FromTimeout(TimeSpan timeout) => new(WaitContext.FromTimeout(timeout));

    /// <summary>Create from a relative timeout + cancellation token.</summary>
    [AllowCopy]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UnitOfWorkContext FromTimeout(TimeSpan timeout, CancellationToken token) => new(WaitContext.FromTimeout(timeout, token));

    /// <summary>
    /// Unbounded context: infinite deadline, no cancellation. For internal operations
    /// that should not be subject to timeout (e.g., cleanup, rollback).
    /// </summary>
    [AllowCopy]
    public static UnitOfWorkContext None
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(Deadline.Infinite, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // Yield-Point Check
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Cooperative cancellation check. Call at yield points (safe locations where aborting won't leave data structures in an inconsistent state).
    /// </summary>
    /// <remarks>
    /// <para>If <see cref="_holdoffCount"/> &gt; 0 (inside a holdoff region), this is a no-op. Cancellation is deferred until the holdoff exits.</para>
    /// <para>Checks deadline first (cheaper than token check on most paths), then cancellation token. Accesses fields through embedded
    /// <see cref="WaitContext"/> — the JIT resolves struct field offsets at compile time, producing identical code to direct field access.</para>
    /// </remarks>
    /// <exception cref="TyphonTimeoutException">Deadline has expired (outside holdoff).</exception>
    /// <exception cref="OperationCanceledException">Token was canceled (outside holdoff).</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ThrowIfCancelled()
    {
        if (_holdoffCount > 0)
        {
            return;
        }

        if (WaitContext.Deadline.IsExpired)
        {
            ThrowDeadlineExpired();
        }

        WaitContext.Token.ThrowIfCancellationRequested();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowDeadlineExpired() => 
        throw new TyphonTimeoutException(TyphonErrorCode.TransactionTimeout, "Operation deadline expired", WaitContext.Deadline.Remaining);

    // ═══════════════════════════════════════════════════════════════
    // Holdoff — Critical Section Protection
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Enter a holdoff region. While in holdoff, <see cref="ThrowIfCancelled"/> is a no-op. Returns a disposable scope guard. Supports nesting.
    /// </summary>
    /// <example>
    /// <code>
    /// using var holdoff = ctx.EnterHoldoff();
    /// // Critical section — cancellation deferred
    /// SplitBTreeNode(ref ctx);
    /// // holdoff.Dispose() decrements counter
    /// </code>
    /// </example>
    [UnscopedRef]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal HoldoffScope EnterHoldoff() => new(ref this);

    /// <summary>Increment holdoff counter (prefer <see cref="EnterHoldoff"/> for RAII safety).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginHoldoff() => _holdoffCount++;

    /// <summary>Decrement holdoff counter.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndHoldoff()
    {
        Debug.Assert(_holdoffCount > 0, "EndHoldoff called without matching BeginHoldoff");
        _holdoffCount--;
    }

    // ═══════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The embedded deadline.</summary>
    public Deadline Deadline
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => WaitContext.Deadline;
    }

    /// <summary>The embedded cancellation token.</summary>
    public CancellationToken Token
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => WaitContext.Token;
    }

    /// <summary>True if the deadline has expired.</summary>
    public bool IsExpired
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => WaitContext.Deadline.IsExpired;
    }

    /// <summary>True if the cancellation token has been triggered.</summary>
    public bool IsCancellationRequested
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => WaitContext.Token.IsCancellationRequested;
    }

    /// <summary>True if currently inside a holdoff region.</summary>
    public bool IsInHoldoff
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _holdoffCount > 0;
    }

    /// <summary>Remaining time until deadline expires.</summary>
    public TimeSpan Remaining
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => WaitContext.Deadline.Remaining;
    }
}
