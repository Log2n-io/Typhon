using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-thread epoch slot padded to one cache line (64 bytes) to eliminate false sharing between threads that pin/unpin concurrently.
/// </summary>
/// <remarks>
/// <para><c>PinnedEpoch</c> and <c>Depth</c> are hot (written on every scope enter/exit). <c>SlotState</c> is warm (CAS on claim/free). All three share the
/// same cache line for the owning thread, but different threads' slots are isolated.</para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct PaddedEpochSlot
{
    [FieldOffset(0)]  public long PinnedEpoch;   // Hot: written on every scope enter/exit
    [FieldOffset(8)]  public int  Depth;          // Warm: read/written on every scope enter/exit
    [FieldOffset(12)] public int  SlotState;      // CAS on claim/free (replaces _slotStates[i])
    // Bytes 16-63: implicit padding to fill cache line
}

/// <summary>
/// Fixed-size registry of per-thread epoch pins. Each thread claims a slot on first use and releases it on thread death (via <see cref="EpochSlotHandle"/>
/// CriticalFinalizerObject).
/// </summary>
/// <remarks>
/// <para>Memory layout: one cache-line-padded slot per thread (64 bytes each) to eliminate false sharing between concurrent pin/unpin operations on different
/// threads.</para>
/// <para><c>_ownerThreads[256]</c> is cold (accessed only on registration and liveness checks) and kept as a separate reference array.</para>
/// <para><b>Thread-static binding:</b> A thread can only be registered in one registry at a time. If a thread encounters a different registry instance
/// (e.g., in tests that create multiple EpochManagers), it re-registers in the new one. Per ADR-004, production has a single DatabaseEngine per process,
/// so this is only relevant for testing.</para>
/// </remarks>
internal sealed class EpochThreadRegistry : IDisposable
{
    // ═══════════════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════════════

    public const int MaxSlots = 256;

    // int (not byte) because Interlocked.CompareExchange has no byte overload
    internal const int SlotFree = 0;
    internal const int SlotActive = 1;

    // ═══════════════════════════════════════════════════════════════════════
    // Padded Slot Storage
    // ═══════════════════════════════════════════════════════════════════════

    // One cache-line-padded slot per thread: PinnedEpoch + Depth + SlotState in 64 bytes.
    // 256 slots × 64 bytes = 16KB. Eliminates false sharing between threads.
    private readonly PaddedEpochSlot[] _slots = new PaddedEpochSlot[MaxSlots];

    // Cold: registration and liveness checks.
    // Kept separate because Thread is a reference type (can't embed in unmanaged StructLayout).
    private readonly Thread[] _ownerThreads = new Thread[MaxSlots];

    // Slot allocation tracking
    private int _highWaterMark;  // Next slot to try for allocation (grows monotonically)
    private int _activeSlotCount;

    // Per-thread slot index (O(1) lookup after first registration).
    // _threadRegistry tracks which registry instance the thread's slot belongs to,
    // allowing re-registration when a thread encounters a different instance (e.g., in tests).
    [ThreadStatic]
    private static int _threadSlotIndex;

    [ThreadStatic]
    private static EpochThreadRegistry _threadRegistry;

    // Roots the handle to prevent premature GC — must live as long as the thread.
    // ReSharper disable once NotAccessedField.Local
    [ThreadStatic]
    private static EpochSlotHandle _threadSlotHandle;

    /// <summary>Number of slots currently owned by active threads.</summary>
    public int ActiveSlotCount => _activeSlotCount;

    /// <summary>
    /// Returns true if the current thread is inside an epoch scope (depth &gt; 0).
    /// </summary>
    public bool IsCurrentThreadInScope => _threadRegistry == this && _slots[_threadSlotIndex].Depth > 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Slot Management
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pin the current thread to the given epoch. Claims a slot on first call.
    /// </summary>
    /// <returns>Nesting depth before this call (0 = outermost).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PinCurrentThread(long epoch)
    {
        if (_threadRegistry != this)
        {
            ClaimSlot();
        }

        var slot = _threadSlotIndex;
        var depth = _slots[slot].Depth;
        _slots[slot].Depth = depth + 1;

        if (depth == 0)
        {
            // Outermost scope: pin to current epoch
            _slots[slot].PinnedEpoch = epoch;
        }

        return depth;
    }

    /// <summary>
    /// Unpin the current thread if this is the outermost scope exit.
    /// </summary>
    /// <param name="expectedDepth">Depth returned by the matching <see cref="PinCurrentThread"/>.</param>
    /// <returns>True if this was the outermost scope exit (thread is now unpinned).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool UnpinCurrentThread(int expectedDepth)
    {
        var slot = _threadSlotIndex;
        var currentDepth = _slots[slot].Depth;

        // Depth validation: catch copy-safety violations
        if (currentDepth != expectedDepth + 1)
        {
            ThrowHelper.ThrowInvalidOp(
                $"EpochGuard depth mismatch: expected {expectedDepth + 1}, got {currentDepth}. " +
                "Probable cause: EpochGuard was copied or disposed out of order.");
        }

        _slots[slot].Depth = expectedDepth;

        if (expectedDepth == 0)
        {
            // Outermost scope: unpin
            _slots[slot].PinnedEpoch = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Unpin the current thread without enforcing LIFO ordering. Decrements the nesting depth and unpins when it reaches zero.
    /// Used by <see cref="Transaction"/> which can be disposed in any order (not just LIFO).
    /// </summary>
    /// <returns>True if this was the outermost scope exit (thread is now unpinned).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool UnpinCurrentThreadUnordered()
    {
        var slot = _threadSlotIndex;
        var currentDepth = _slots[slot].Depth;

        if (currentDepth <= 0)
        {
            ThrowHelper.ThrowInvalidOp("Epoch scope underflow: attempted to exit scope when not in any scope.");
        }

        _slots[slot].Depth = currentDepth - 1;

        if (currentDepth == 1)
        {
            // Was the outermost scope: unpin
            _slots[slot].PinnedEpoch = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Atomically update the current thread's pinned epoch without going through the unpinned state.
    /// The thread remains continuously pinned — no brief window where MinActiveEpoch jumps.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RefreshPinnedEpoch(long newEpoch)
    {
        Debug.Assert(_threadRegistry == this, "RefreshPinnedEpoch called on wrong registry");
        var slot = _threadSlotIndex;
        Debug.Assert(_slots[slot].Depth > 0, "RefreshPinnedEpoch called outside epoch scope");
        _slots[slot].PinnedEpoch = newEpoch;
    }

    /// <summary>
    /// Claim a free slot for the current thread. Called once per thread lifetime (or when a thread encounters a new registry instance).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]  // Keep hot path (PinCurrentThread) small
    private void ClaimSlot()
    {
        var thread = Thread.CurrentThread;

        // Scan from high water mark first (fast path: no contention on new slots)
        for (int attempts = 0; attempts < MaxSlots; attempts++)
        {
            var candidate = Interlocked.Increment(ref _highWaterMark) - 1;
            if (candidate >= MaxSlots)
            {
                // Wrap around — need to reclaim dead thread slots
                break;
            }

            if (TryClaimSlot(candidate, thread))
            {
                return;
            }
        }

        // Slow path: scan all slots looking for dead-thread slots to reclaim
        for (int i = 0; i < MaxSlots; i++)
        {
            if (_slots[i].SlotState == SlotActive)
            {
                var owner = _ownerThreads[i];
                if (owner != null && !owner.IsAlive)
                {
                    // Thread died without cleanup — reclaim the slot
                    if (TryReclaimDeadSlot(i, thread))
                    {
                        return;
                    }
                }
            }
            else if (_slots[i].SlotState == SlotFree)
            {
                if (TryClaimSlot(i, thread))
                {
                    return;
                }
            }
        }

        ThrowHelper.ThrowEpochRegistryExhausted();
    }

    private bool TryClaimSlot(int index, Thread thread)
    {
        if (Interlocked.CompareExchange(ref _slots[index].SlotState, SlotActive, SlotFree) == SlotFree)
        {
            _ownerThreads[index] = thread;
            _slots[index].PinnedEpoch = 0;
            _slots[index].Depth = 0;
            _threadSlotIndex = index;
            _threadRegistry = this;
            var newCount = Interlocked.Increment(ref _activeSlotCount);

            // Register finalizer for thread death cleanup.
            // Store in [ThreadStatic] field to root the handle for the thread's lifetime.
            _threadSlotHandle = new EpochSlotHandle(this, index);
            TyphonEvent.EmitConcurrencyEpochSlotClaim((ushort)index, (ushort)thread.ManagedThreadId, (ushort)newCount);
            return true;
        }

        return false;
    }

    private bool TryReclaimDeadSlot(int index, Thread newOwner)
    {
        // The slot is marked active but the owning thread is dead.
        // Use CAS on the owner thread reference to claim it atomically.
        var oldOwner = _ownerThreads[index];
        if (oldOwner != null && !oldOwner.IsAlive &&
            Interlocked.CompareExchange(ref _ownerThreads[index], newOwner, oldOwner) == oldOwner)
        {
            _slots[index].PinnedEpoch = 0;
            _slots[index].Depth = 0;
            _threadSlotIndex = index;
            _threadRegistry = this;
            // activeSlotCount doesn't change — we're reusing an active slot

            // Root the handle for this thread
            _threadSlotHandle = new EpochSlotHandle(this, index);
            TyphonEvent.EmitConcurrencyEpochSlotReclaim((ushort)index, (ushort)oldOwner.ManagedThreadId, (ushort)newOwner.ManagedThreadId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Release a slot. Called by <see cref="EpochSlotHandle"/> finalizer on thread death.
    /// </summary>
    internal void FreeSlot(int index)
    {
        _slots[index].PinnedEpoch = 0;
        _slots[index].Depth = 0;
        _ownerThreads[index] = null;

        // CAS to make FreeSlot idempotent: ComputeMinActiveEpoch and EpochSlotHandle finalizer
        // can both call FreeSlot for the same dead-thread slot concurrently.
        if (Interlocked.CompareExchange(ref _slots[index].SlotState, SlotFree, SlotActive) == SlotActive)
        {
            Interlocked.Decrement(ref _activeSlotCount);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MinActiveEpoch
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the minimum epoch pinned by any active thread. Returns <paramref name="currentGlobalEpoch"/> if no threads are pinned.
    /// </summary>
    /// <remarks>
    /// Scans up to <c>_highWaterMark</c> slots (capped at MaxSlots after wrap-around). Each PaddedEpochSlot is 64 bytes (one cache line).
    /// For typical workloads (few threads), this scans only a handful of cache lines instead of all 256.
    /// </remarks>
    public long ComputeMinActiveEpoch(long currentGlobalEpoch)
    {
        var min = currentGlobalEpoch;

        // Scan only allocated slots: _highWaterMark grows monotonically on allocation.
        // After wrap-around (256+ threads lifetime), Math.Min caps at MaxSlots.
        var scanLimit = Math.Min(Volatile.Read(ref _highWaterMark), MaxSlots);
        for (int i = 0; i < scanLimit; i++)
        {
            var pinned = _slots[i].PinnedEpoch;
            if (pinned == 0)
            {
                continue;
            }

            // Liveness check: if the thread died, clear the slot
            if (_slots[i].SlotState == SlotActive)
            {
                var thread = _ownerThreads[i];
                if (thread != null && !thread.IsAlive)
                {
                    FreeSlot(i);
                    continue;
                }
            }

            if (pinned < min)
            {
                min = pinned;
            }
        }

        return min;
    }

    public void Dispose()
    {
        // Clear all slots — finalizers may still run but will see SlotFree
        for (int i = 0; i < MaxSlots; i++)
        {
            _slots[i].SlotState = SlotFree;
            _slots[i].PinnedEpoch = 0;
            _ownerThreads[i] = null;
        }
    }
}

/// <summary>
/// CriticalFinalizerObject attached to each thread that claims an epoch slot.
/// When the thread dies (and GC collects the ThreadStatic reference), the finalizer releases the slot back to the registry.
/// </summary>
internal sealed class EpochSlotHandle : System.Runtime.ConstrainedExecution.CriticalFinalizerObject
{
    private readonly EpochThreadRegistry _registry;
    private readonly int _slotIndex;

    internal EpochSlotHandle(EpochThreadRegistry registry, int slotIndex)
    {
        _registry = registry;
        _slotIndex = slotIndex;
    }

    ~EpochSlotHandle()
    {
        _registry.FreeSlot(_slotIndex);
    }
}
