using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.internals;

/// <summary>
/// Per-archetype subscription list for views whose membership is the archetype's whole live set — the channel an unfiltered
/// <c>Query&lt;TArchetype&gt;().ToView()</c> registers on (#790).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not <see cref="ViewRegistry"/>.</b> That one is <c>ViewRegistration[][]</c> indexed by a component's indexed-field
/// number, and its entries are published from inside the per-indexed-field loop of the spawn/destroy commit path — the change feed is a
/// by-product of index maintenance. An archetype whose components carry no <c>[Index]</c> has an empty field loop, so it publishes to
/// nobody and there is no field number to register under. This registry is keyed by ARCHETYPE and published from the two commit loops
/// that walk every spawned and destroyed entity regardless of indexing.
/// </para>
/// <para>
/// <b>Concurrency.</b> Copy-on-write array, mirroring <see cref="ViewRegistry"/>: lock-free read on the commit hot path, locked write on
/// the cold registration path.
/// </para>
/// </remarks>
internal sealed class ArchetypeMembershipRegistry
{
    private readonly Lock _writeLock = new();
    private ViewRegistration[] _views = [];

    /// <summary>
    /// Monotonic counter of COMMITS that spawned or destroyed at least one entity in this archetype — not of entities, so a 50 000-entity
    /// batch moves it by one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refresh gate reads this and returns immediately when it has not moved, which is what makes a view over a quiet archetype cost a
    /// load and a compare instead of a scan. That is the dominant term at realistic view counts: a simulation holds tens of views and most
    /// archetypes are untouched on most ticks.
    /// </para>
    /// <para>
    /// <b>MEMB-01 — this is released AFTER the entries it accounts for.</b> <see cref="Bump"/> is an <c>Interlocked.Increment</c>, a full
    /// fence on x64 and arm64, so the <c>TryAppend</c>s cannot sink past it; the reader's acquire load pairs with that. Reversed, a view
    /// reads the new value, drains an empty buffer, records it as consumed, and never sees those entities — silent and permanent. It is
    /// also bumped before the commit publishes its TSN (end of <c>FlushEcsPendingOperations</c>, ahead of <c>WaitAndFinalize</c>), so a
    /// reader whose snapshot can see the commit can also see the counter move.
    /// </para>
    /// </remarks>
    private long _structuralEpoch;

    /// <summary>Acquire-read of the structural epoch. See the remark on the backing field for the ordering it participates in.</summary>
    internal long StructuralEpoch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _structuralEpoch);
    }

    /// <summary>Records that one commit structurally changed this archetype. Call AFTER every membership entry for that commit is appended.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Bump() => Interlocked.Increment(ref _structuralEpoch);

    /// <summary>The registered views. Lock-free; the returned span is a snapshot of one copy-on-write generation.</summary>
    /// <remarks>
    /// <b>Acquire, not a plain load.</b> <see cref="Register"/> release-writes the new array so every field of the new registration is visible before the
    /// reference is; a plain load here would leave that release with no matching acquire, and on arm64 nothing stops the reads of a registration's
    /// <c>DeltaBuffer</c> from floating above it. Free on x64, where acquire folds to a plain <c>mov</c>.
    /// </remarks>
    internal ReadOnlySpan<ViewRegistration> Views
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _views);
    }

    /// <summary>True when no view subscribes — the publisher's early-out, checked before it does any per-entity work.</summary>
    internal bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _views).Length == 0;
    }

    /// <summary>
    /// Number of subscribed views. Exposed because nothing else surfaces it: a view is removed only by <see cref="Deregister"/>, reached only from
    /// <c>ViewBase.Dispose</c>, so an abandoned view stays subscribed for the life of the engine and every commit keeps paying for it. Before #790 an
    /// unfiltered <c>ToView()</c> registered with nothing, so an undisposed one was plain garbage; now it is a leak with a per-commit cost, and a count is
    /// the minimum that makes it diagnosable.
    /// </summary>
    internal int ViewCount => Volatile.Read(ref _views).Length;

    /// <summary>
    /// The current subscriber array, for a publisher that must treat one commit as all-or-nothing per view.
    /// </summary>
    /// <remarks>
    /// Returns the array itself, not a copy — it is immutable by construction (copy-on-write; <see cref="Register"/> and
    /// <see cref="Deregister"/> replace the reference and never mutate in place), so holding it for the length of a commit is safe and free.
    /// </remarks>
    internal ViewRegistration[] ViewsSnapshot() => Volatile.Read(ref _views);

    /// <summary>Subscribe a view. Idempotent on the view id.</summary>
    internal void Register(IView view, ViewDeltaRingBuffer deltaBuffer)
    {
        lock (_writeLock)
        {
            SweepAbandonedLocked();
            var existing = _views;
            for (var i = 0; i < existing.Length; i++)
            {
                if (existing[i].ViewId == view.ViewId)
                {
                    return;
                }
            }

            var updated = new ViewRegistration[existing.Length + 1];
            Array.Copy(existing, updated, existing.Length);
            updated[existing.Length] = new ViewRegistration(view.ViewId, deltaBuffer, 0);
            // Release: the publisher reads this array with no lock, and every field of the new registration must be visible to it before the
            // reference is.
            Volatile.Write(ref _views, updated);
        }
    }


    /// <summary>Unsubscribe a view. No-op when it was never registered.</summary>
    /// <remarks>
    /// Takes no latch, and deliberately: the publisher is not excluded from this, it is made HARMLESS. <c>ViewBase.Dispose</c> RETIRES the view's
    /// pinned buffer to the engine's <c>ViewBufferReclaimer</c> rather than freeing it, so a publisher already holding a snapshot that names this
    /// view writes into live mapped memory. See MEMB-04.
    /// </remarks>
    internal void Deregister(IView view)
    {
        // No latch. The publisher is no longer excluded from this — it is made HARMLESS instead: ViewBase.Dispose retires the view's pinned buffer
        // to the engine's ViewBufferReclaimer rather than freeing it, so a publisher already past its IsDisposed check writes into live mapped
        // memory. The exclusive latch that used to stand here waited up to a full DefaultCommitTimeout PER ARCHETYPE and then, on timeout, freed
        // the buffer unlatched anyway — it traded a memory-safety hazard for a liveness hazard and kept both. See MEMB-04.
        lock (_writeLock)
        {
            SweepAbandonedLocked();
            var existing = _views;
            var index = -1;
            for (var i = 0; i < existing.Length; i++)
            {
                if (existing[i].ViewId == view.ViewId)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return;
            }

            if (existing.Length == 1)
            {
                Volatile.Write(ref _views, []);
                return;
            }

            var updated = new ViewRegistration[existing.Length - 1];
            Array.Copy(existing, 0, updated, 0, index);
            Array.Copy(existing, index + 1, updated, index, existing.Length - index - 1);
            Volatile.Write(ref _views, updated);
        }
    }

    /// <summary>
    /// Removes every registration whose view was finalized without <c>Dispose</c> and retires its buffer (#865) — the same sweep as
    /// <c>ViewRegistry</c>'s, on every registration and deregistration. Caller holds <see cref="_writeLock"/>.
    /// </summary>
    private void SweepAbandonedLocked()
    {
        var existing = _views;
        var removeCount = 0;
        for (var i = 0; i < existing.Length; i++)
        {
            if (existing[i].DeltaBuffer.IsAbandoned)
            {
                removeCount++;
            }
        }

        if (removeCount == 0)
        {
            return;
        }

        var updated = removeCount == existing.Length ? [] : new ViewRegistration[existing.Length - removeCount];
        var k = 0;
        for (var i = 0; i < existing.Length; i++)
        {
            if (!existing[i].DeltaBuffer.IsAbandoned)
            {
                updated[k++] = existing[i];
            }
        }

        // Abandonment is monotonic, so a view abandoned between the two passes can only shorten the copy, never overrun it.
        Volatile.Write(ref _views, k == updated.Length ? updated : updated[..k]);

        // Retire after the entry left the array — see ViewRegistry.SweepAbandonedLocked.
        for (var i = 0; i < existing.Length; i++)
        {
            if (existing[i].DeltaBuffer.IsAbandoned && !Contains(_views, existing[i].ViewId))
            {
                existing[i].DeltaBuffer.RetireAbandoned();
            }
        }
    }

    private static bool Contains(ViewRegistration[] regs, int viewId)
    {
        for (var i = 0; i < regs.Length; i++)
        {
            if (regs[i].ViewId == viewId)
            {
                return true;
            }
        }

        return false;
    }
}
