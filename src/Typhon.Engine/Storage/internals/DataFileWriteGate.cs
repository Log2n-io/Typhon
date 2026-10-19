using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The data-file write gate (BKP-01): every data-file write holds it shared from before its change-map update until the write has completed;
/// online backup's freeze holds it exclusive for the few microseconds it takes to fix the image's instant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="AccessControl"/>.</b> Its shared count is 8 bits with only a <c>Debug.Assert</c> on overflow, and an async
/// <see cref="PagedMMF.SavePages"/> batch holds the gate across its I/O, so more than 255 holders is reachable. <see cref="AccessControlSmall"/>
/// has no writer preference, so a freeze waiting behind a stream of checkpoint writes would starve. Both spin an exclusive waiter forever, and the
/// engine must never wait on a backup past a bound (BKP-12).
/// </para>
/// <para>
/// <b>Shape.</b> Shared holders are counted on <see cref="StripeCount"/> cache-line-padded stripes picked by thread id, so the four parallel wave
/// writers do not bounce one line. A shared exit names its stripe in the <see cref="Token"/>, so an async write may be exited by the pool thread that
/// completes it. The freezer raises <c>_freeze</c>, then waits for every stripe to reach zero.
/// </para>
/// <para>
/// <b>Why it is correct on x64 and arm64 (Dekker).</b> A writer's <c>Interlocked.Increment</c> and the freezer's <c>Interlocked.Exchange</c> are
/// full fences on both architectures, and each is followed by a load of the other side's variable, so at least one side sees the other: the writer
/// backs off, or the freezer waits for it. <see cref="ExitExclusive"/> publishes with a release store that the writer's acquire load of
/// <c>_freeze</c> pairs with, so whatever the freezer stored inside the exclusive section (map swap, hook install) is visible to every writer that
/// enters after it. <b>Writer preference:</b> once <c>_freeze</c> is raised no new shared enter succeeds, so the freezer waits only for the writes
/// already in flight. <b>Bounded:</b> <see cref="TryEnterExclusive"/> gives up after its timeout and reopens the gate.
/// </para>
/// <para>
/// A thread must not enter shared while it holds a shared token whose release depends on that same thread: behind a pending freeze the second
/// enter waits for the first exit. Every holder today releases on its own (a synchronous write, or an async batch completed by the OS).
/// </para>
/// </remarks>
internal sealed class DataFileWriteGate
{
    /// <summary>Number of shared-holder stripes; a power of two.</summary>
    internal const int StripeCount = 16;

    private readonly CacheLinePaddedLong[] _stripes = new CacheLinePaddedLong[StripeCount];
    private CacheLinePaddedLong _freeze;
    private readonly Lock _freezer = new();

    /// <summary>A shared hold, naming the stripe it was counted on.</summary>
    internal readonly struct Token
    {
        internal Token(int stripe) => Stripe = stripe;

        /// <summary>The stripe this hold was counted on.</summary>
        internal int Stripe { get; }
    }

    /// <summary>Test hook: invoked when a shared enter has found a freeze pending and backed off. Null in production.</summary>
    internal Action BackedOffProbe { get; set; }

    /// <summary>Whether a freeze is requested or held.</summary>
    internal bool FreezePending => Volatile.Read(ref _freeze.Value) != 0;

    /// <summary>Whether the calling thread holds the gate exclusive.</summary>
    internal bool IsExclusiveHeldByCurrentThread => _freezer.IsHeldByCurrentThread && FreezePending;

    /// <summary>Enters shared. Waits only while a freeze is requested or held.</summary>
    internal Token EnterShared()
    {
        var stripe = Environment.CurrentManagedThreadId & (StripeCount - 1);
        ref var counter = ref _stripes[stripe].Value;
        while (true)
        {
            Interlocked.Increment(ref counter);
            if (Volatile.Read(ref _freeze.Value) == 0)
            {
                return new Token(stripe);
            }

            Interlocked.Decrement(ref counter);
            BackedOffProbe?.Invoke();
            var spin = new SpinWait();
            while (Volatile.Read(ref _freeze.Value) != 0)
            {
                // No Sleep(1): it is 15.6 ms on Windows, and a freeze lasts microseconds once the writes in flight have drained.
                spin.SpinOnce(-1);
            }
        }
    }

    /// <summary>Leaves shared. Any thread may call it with the token of a hold another thread took.</summary>
    internal void ExitShared(Token token) => Interlocked.Decrement(ref _stripes[token.Stripe].Value);

    /// <summary>
    /// Enters exclusive: raises the freeze, then waits for every shared holder to leave. On timeout, reopens the gate and returns <c>false</c>; the
    /// caller (a backup) fails, the engine never waits longer (BKP-12).
    /// </summary>
    /// <param name="acquireTimeout">How long to wait for the writes in flight.</param>
    internal bool TryEnterExclusive(TimeSpan acquireTimeout)
    {
        _freezer.Enter();
        Interlocked.Exchange(ref _freeze.Value, 1);
        var deadline = Stopwatch.GetTimestamp() + (long)(acquireTimeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (SharedHolders() != 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                Volatile.Write(ref _freeze.Value, 0);
                _freezer.Exit();
                return false;
            }

            spin.SpinOnce(-1);
        }

        return true;
    }

    /// <summary>Leaves exclusive. Everything stored before this call is visible to every writer that enters after it.</summary>
    internal void ExitExclusive()
    {
        Debug.Assert(_freezer.IsHeldByCurrentThread, "ExitExclusive without TryEnterExclusive on this thread");
        Volatile.Write(ref _freeze.Value, 0);
        _freezer.Exit();
    }

    /// <summary>The number of shared holds, summed over the stripes. Exact only while the gate is held exclusive.</summary>
    internal long SharedHolders()
    {
        long total = 0;
        for (var i = 0; i < StripeCount; i++)
        {
            total += Volatile.Read(ref _stripes[i].Value);
        }

        return total;
    }
}
