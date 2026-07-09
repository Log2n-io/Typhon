using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Static registry of 256 <see cref="ThreadSlot"/>s, one per producer thread. The foundation of the Tracy-style profiler's capture layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Model:</b> fixed-size array, monotonic <c>_highWaterMark</c> for scan optimization, CAS-claim Free→Active, finalizer-triggered retirement via
/// <see cref="SlotReleaser"/>. Mirrors the structural pattern of <c>EpochThreadRegistry</c> but with distinct semantics (slot ownership vs. epoch pinning).
/// </para>
/// <para>
/// <b>Cache layout:</b> <c>s_slots</c> is a flat array of <see cref="PaddedSlot"/> structs, each exactly 64 bytes. The consumer-written
/// <see cref="PaddedSlot.State"/> lives in the padded struct itself, one cache line per slot — no false sharing between adjacent slot states.
/// Producer-hot fields (<see cref="ThreadSlot.Buffer"/>, <see cref="ThreadSlot.CaptureActivityContext"/>) live on the referenced
/// <see cref="ThreadSlot"/> heap object, on a different cache line than the CAS-touched state.
/// </para>
/// <para>
/// <b>Lazy buffer allocation:</b> the 256 <see cref="ThreadSlot"/> stubs are allocated at class init (~25 KB total — just headers + a couple of fields
/// each). The ~128 KB <see cref="TraceRecordRing"/> backing array is allocated inside <c>ClaimSlot</c> on first claim and reused across re-claims
/// of the same slot. Unused slots stay null-buffer and consume no event memory.
/// </para>
/// <para>
/// <b>Hot path:</b> the most common call is <c>ThreadSlotRegistry.GetOrAssignSlot()</c> from within <c>TyphonEvent.BeginSpan</c>. After the first
/// successful claim, <c>[ThreadStatic] t_slotIndex</c> holds the slot index and subsequent calls return it with a single int read and a branch.
/// </para>
/// <para>
/// <b>Memory ordering:</b> plain field access everywhere. On x64, reads and writes of ≤64-bit primitives are naturally atomic (per CLAUDE.md). The
/// only synchronization primitive on the registry hot path is <see cref="Interlocked.CompareExchange(ref int, int, int)"/> for slot-state transitions
/// and high-water-mark advancement, which are genuine read-modify-write sequences that need atomicity.
/// </para>
/// </remarks>
internal static class ThreadSlotRegistry
{
    /// <summary>Maximum number of concurrent live producer threads the profiler can track. Chosen to massively exceed realistic Typhon workloads.</summary>
    public const int MaxSlots = 256;

    /// <summary>
    /// Default per-slot variable-size SPSC ring buffer capacity in bytes. 4 MB holds ~100K minimum-size records — deep headroom to
    /// absorb any realistic burst (full IOProfileRunner workload, or a tight spawn-loop emitting hundreds of thousands of EcsSpawn
    /// records in &lt;1s) without the producer hitting drop-newest. Size progression: 128 KB → 1 MB → 4 MB as progressively heavier
    /// workloads uncovered producer-side ring-full drops at each tier.
    /// </summary>
    /// <remarks>
    /// <b>Memory cost:</b> rings are allocated lazily per claimed slot. At typical usage (5-10 active threads) that's 20-40 MB of
    /// pinned memory. Worst case (all 256 slots active) = 1 GB, far beyond any realistic thread count but the ceiling exists. Modern
    /// servers commit tens of GB of RAM; this buffer size is well within the "over-buffer to avoid drop" priority for observability.
    /// </remarks>
    private const int DefaultBufferCapacity = 1 * 1024 * 1024;

    private static readonly PaddedSlot[] SSlots = new PaddedSlot[MaxSlots];
    private static int SHighWaterMark;
    private static int SActiveSlotCount;
    private static bool SGlobalCaptureActivityContext = true;

    /// <summary>Per-thread slot index. The paired <see cref="Releaser"/> non-null is the "this thread has a claim" sentinel.</summary>
    [ThreadStatic]
    private static int SlotIndex;

    /// <summary>
    /// Per-thread handle rooting the <see cref="SlotReleaser"/> for the lifetime of the thread. When the thread dies and the GC collects the
    /// TLS root, the releaser's finalizer runs and marks the slot <see cref="SlotState.Retiring"/>. Non-null also serves as the "this thread has a
    /// claim" sentinel since <see cref="SlotIndex"/> defaults to 0 on fresh threads (a valid slot index).
    /// </summary>
    // ReSharper disable once NotAccessedField.Local
    [ThreadStatic]
    private static SlotReleaser Releaser;

    static ThreadSlotRegistry()
    {
        // Allocate the ThreadSlot stub for each array entry. The heavy TraceEvent[2048] buffer is NOT allocated here —
        // it's lazy-allocated in ClaimSlot on first claim so idle slots consume ~30 B (object header + fields) instead of 128 KB.
        for (var i = 0; i < MaxSlots; i++)
        {
            SSlots[i].Slot = new ThreadSlot();
            // State defaults to 0 == SlotState.Free, which is correct.
        }
    }

    /// <summary>
    /// Total number of claimed (Active or Retiring) slots across the registry. Cheap diagnostic counter, not a correctness primitive.
    /// </summary>
    public static int ActiveSlotCount => SActiveSlotCount;

    /// <summary>
    /// High-water mark: the highest slot index that has ever been claimed + 1. Scan upper bound for <see cref="GetOrAssignSlot"/> to avoid touching
    /// unused cache lines on modest workloads.
    /// </summary>
    public static int HighWaterMark => SHighWaterMark;

    /// <summary>
    /// Global default for <see cref="ThreadSlot.CaptureActivityContext"/>. Each new claim inherits this value; individual threads can override via
    /// <c>TyphonEvent.SuppressActivityContextOnThisThread</c>.
    /// </summary>
    public static bool GlobalCaptureActivityContext
    {
        get => SGlobalCaptureActivityContext;
        set => SGlobalCaptureActivityContext = value;
    }

    /// <summary>
    /// Returns the current thread's slot, allocating one via CAS if this is the thread's first emit. Returns -1 if the registry is full.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetOrAssignSlot()
    {
        // t_releaser != null is the "this thread has a claim" sentinel. t_slotIndex defaults to 0 on fresh threads (a valid index),
        // so we cannot use index-based sentinels — the releaser reference is the single source of truth.
        if (Releaser != null)
        {
            return SlotIndex;
        }
        return ClaimSlot();
    }

    /// <summary>Returns true if the current thread has a claimed slot. Useful for diagnostics and tests.</summary>
    public static bool IsSlotAssigned => Releaser != null;

    /// <summary>Current thread's claimed slot index, or -1 if not claimed. Prefer <see cref="GetOrAssignSlot"/> on hot paths.</summary>
    public static int CurrentThreadSlotIndex => Releaser != null ? SlotIndex : -1;

    /// <summary>
    /// Access to a slot's producer-owned bookkeeping by index. Returned by reference so callers on the consumer thread can read multiple fields
    /// without repeated array indexing.
    /// </summary>
    public static ThreadSlot GetSlot(int index) => SSlots[index].Slot;

    /// <summary>Access to a slot's lifecycle state. Read-only to avoid accidentally bypassing the CAS transitions.</summary>
    public static int GetSlotState(int index) => SSlots[index].State;

    /// <summary>
    /// Look up a slot by OS thread ID. Used by the ETW scheduling pump to map kernel <c>OldThreadID</c> / <c>NewThreadID</c> values (which are OS TIDs) onto
    /// Typhon thread slots, so context-switch events can be re-attributed to the right lane. Returns <c>false</c> if no Active or Retiring slot owns the given
    /// OS TID — the pump drops such events (Typhon-only scope).
    /// </summary>
    /// <remarks>
    /// <b>Hot path:</b> called from the ETW pump for every kernel cswitch event (thousands per second machine-wide). Implemented as a linear scan over the
    /// high-water-mark range — ~30 slots typical, cache-hot sequential access, ~10 ns. A dictionary would add concurrency overhead (the slot pool is mutated by
    /// claiming threads) without a meaningful speedup at this scale.
    /// <para>
    /// <b>Race-tolerance:</b> a slot may be in the middle of <c>AssignClaim</c> or <c>FreeRetiringSlot</c> while the pump scans. Reading stale data is
    /// harmless: if we miss a just-claimed slot, the next cswitch will find it; if we match a just-freed slot, the pump emits an event for a slot the consumer
    /// no longer renders. Both are recoverable — no correctness invariant depends on perfect hand-off here.
    /// </para>
    /// </remarks>
    public static bool TryGetSlotByOsThreadId(uint osThreadId, out int slotIndex)
    {
        if (osThreadId == 0)
        {
            slotIndex = -1;
            return false;
        }
        var hwm = SHighWaterMark;
        for (var i = 0; i < hwm; i++)
        {
            // Read state once. Both Active (1) and Retiring (2) are valid — Retiring slots still have a real OS TID mapping until FreeRetiringSlot transitions
            // to Free, and the cswitch events that happen just before the thread is reaped are exactly the ones we want to capture.
            var state = SSlots[i].State;
            if (state == (int)SlotState.Free)
            {
                continue;
            }
            if (SSlots[i].Slot.OwnerOsThreadId == osThreadId)
            {
                slotIndex = i;
                return true;
            }
        }
        slotIndex = -1;
        return false;
    }

    /// <summary>Windows P/Invoke for <c>GetCurrentThreadId</c>. Returns the OS-level TID of the calling thread.</summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static uint TryGetOsThreadId()
    {
        // On non-Windows platforms the import would still bind (kernel32 isn't present), but we never call it.
        // Stopwatch + Activity TraceIds don't depend on this — the OS TID is purely for the ETW pump's correlation.
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }
        try
        {
            return GetCurrentThreadId();
        }
        catch
        {
            return 0;
        }
    }

    private static int ClaimSlot()
    {
        var threadId = Environment.CurrentManagedThreadId;
        var scanLimit = Math.Min(SHighWaterMark, MaxSlots);

        // Phase 1: scan already-touched slots for a Free one.
        for (var i = 0; i < scanLimit; i++)
        {
            if (SSlots[i].State == (int)SlotState.Free &&
                Interlocked.CompareExchange(ref SSlots[i].State, (int)SlotState.Active, (int)SlotState.Free) == (int)SlotState.Free)
            {
                AssignClaim(i, threadId);
                return i;
            }
        }

        // Phase 2: advance the high-water mark to grab a never-touched slot.
        //
        // CAS the per-slot state BEFORE bumping HWM. Otherwise a concurrent Phase-1 scanner reads the bumped HWM, finds state[hwm]=Free (the byte-array
        // default), and CAS-claims the slot out from under us — both threads end up with SlotIndex=hwm and write to the same ring (SPSC violated).
        while (true)
        {
            var hwm = SHighWaterMark;
            if (hwm >= MaxSlots)
            {
                return -1; // registry full
            }

            if (Interlocked.CompareExchange(ref SSlots[hwm].State, (int)SlotState.Active, (int)SlotState.Free) == (int)SlotState.Free)
            {
                Interlocked.CompareExchange(ref SHighWaterMark, hwm + 1, hwm);
                AssignClaim(hwm, threadId);
                return hwm;
            }

            // Someone else beat us to this index. Advance HWM past it so our next iteration probes the following slot.
            Interlocked.CompareExchange(ref SHighWaterMark, hwm + 1, hwm);
        }
    }

    private static void AssignClaim(int index, int threadId)
    {
        var slot = SSlots[index].Slot;
        // Lazy-allocate the buffer on first claim of this slot. Reused across re-claims of the same index (no realloc).
        if (slot.Buffer == null)
        {
            slot.Buffer = new TraceRecordRing(DefaultBufferCapacity);
        }
        else
        {
            // Re-claim: collapse any leftover spillover chain back to the primary before resetting. The consumer should already have drained and recycled all
            // spillovers before the slot reached Free, but we belt-and-suspenders this in case a Stop-while-warm path or a forced ResetForTests left a chain
            // attached. Walk Buffer.Next forward, returning each spillover to the pool. Buffer itself is never recycled.
            CollapseChainToPrimary(slot);
            slot.Buffer.Reset();
        }
        slot.ChainHead = slot.Buffer;
        slot.ChainTail = slot.Buffer;
        slot.CaptureActivityContext = SGlobalCaptureActivityContext;

        // Resolve name + kind, then publish them BEFORE OwnerManagedThreadId. TcpExporter.BuildCatchupThreadInfoFrame/ uses OwnerManagedThreadId == 0 as the
        // "AssignClaim mid-flight, skip me" sentinel; if we set ManagedThreadId first the catch-up could read a non-zero id while OwnerThreadName /
        // OwnerThreadKind are still defaults and ship a stale record. By writing those two first, any non-zero ManagedThreadId observation implies both the
        // name and kind fields are already populated (x64 store-store ordering).
        var threadName = Thread.CurrentThread.Name;
        var threadKind = ResolveThreadKind(threadId, threadName);
        slot.OwnerThreadName = threadName;
        slot.OwnerThreadKind = threadKind;
        // OS TID is captured on the claiming thread (so GetCurrentThreadId resolves to the right thread). Published BEFORE OwnerManagedThreadId for the same
        // store-store reason as name/kind: the ETW pump does a linear scan keyed on OwnerOsThreadId and only correlates events when the slot is Active —
        // a stale OS TID observed before the slot transitions to Active would still be ignored by the pump (it skips State == Free), but ordering this write
        // before ManagedThreadId keeps the invariant uniform.
        slot.OwnerOsThreadId = TryGetOsThreadId();
        slot.OwnerManagedThreadId = threadId;
        SlotIndex = index;
        Releaser = new SlotReleaser(index);
        Interlocked.Increment(ref SActiveSlotCount);

        // Emit a ThreadInfo record right after the claim so the viewer can label this lane with a real thread name instead of just "Slot N". This runs on the
        // claiming thread, so the per-slot SPSC invariant is preserved (this thread is the sole writer of its ring). If the thread has no name set we pass null
        // — the encoder writes a zero-length name and the viewer falls back to a synthesized label using ThreadKind + managed id.
        TyphonEvent.EmitThreadInfo((byte)index, threadId, threadName, threadKind);
    }

    /// <summary>
    /// Categorize the current thread for the viewer's filter UI. Cascade — first match wins:
    /// (1) the thread that called <c>TyphonProfiler.Start</c> is Main; (2) <c>Typhon.Worker-N</c> threads are
    /// Worker; (3) ThreadPool callbacks are Pool; (4) anything else is Other.
    /// </summary>
    private static ThreadKind ResolveThreadKind(int threadId, string threadName)
    {
        if (threadId == TyphonProfiler.MainThreadId && TyphonProfiler.MainThreadId != 0)
        {
            return ThreadKind.Main;
        }
        if (threadName != null && threadName.StartsWith("Typhon.Worker-", StringComparison.Ordinal))
        {
            return ThreadKind.Worker;
        }
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            return ThreadKind.Pool;
        }
        return ThreadKind.Other;
    }

    /// <summary>
    /// Called by <see cref="SlotReleaser"/>'s finalizer to transition an expiring slot from <see cref="SlotState.Active"/> to
    /// <see cref="SlotState.Retiring"/>. The profiler consumer thread will drain trailing events and release the slot on its next pass.
    /// </summary>
    internal static void MarkRetiring(int slotIndex)
    {
        if ((uint)slotIndex >= MaxSlots)
        {
            return;
        }

        // Only transition Active → Retiring. If the slot was already freed (e.g., by ResetForTests), leave it alone.
        Interlocked.CompareExchange(ref SSlots[slotIndex].State, (int)SlotState.Retiring, (int)SlotState.Active);
    }

    /// <summary>
    /// Transition a retiring slot to <see cref="SlotState.Free"/>. Called by the profiler consumer thread after draining the slot's trailing events.
    /// </summary>
    internal static void FreeRetiringSlot(int slotIndex)
    {
        if ((uint)slotIndex >= MaxSlots)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref SSlots[slotIndex].State, (int)SlotState.Free, (int)SlotState.Retiring) == (int)SlotState.Retiring)
        {
            SSlots[slotIndex].Slot.OwnerManagedThreadId = 0;
            SSlots[slotIndex].Slot.OwnerOsThreadId = 0;
            SSlots[slotIndex].Slot.OwnerThreadName = null;
            SSlots[slotIndex].Slot.OwnerThreadKind = ThreadKind.Other;
            Interlocked.Decrement(ref SActiveSlotCount);
        }
    }

    /// <summary>
    /// Reset the entire registry to a pristine state. Tests only — must not be called while any producer thread is emitting events or any consumer
    /// thread is draining. Clears all slot state and the calling thread's TLS claim. Other threads' TLS cannot be reached from here: if your test
    /// has OTHER live producer threads, each one must call <see cref="ReleaseCurrentThreadForTests"/> before this reset runs, or use fresh threads
    /// after it. Failing to do so leaves those threads with a stale <see cref="SlotIndex"/> and a non-null <see cref="Releaser"/>, which would
    /// silently break SPSC safety if two threads end up on the same reclaimed slot.
    /// </summary>
    internal static void ResetForTests()
    {
        for (var i = 0; i < MaxSlots; i++)
        {
            SSlots[i].State = (int)SlotState.Free;
            var slot = SSlots[i].Slot;
            slot.OwnerManagedThreadId = 0;
            slot.OwnerOsThreadId = 0;
            slot.OwnerThreadName = null;
            slot.OwnerThreadKind = ThreadKind.Other;
            slot.CaptureActivityContext = true;
            // Release any held spillovers back to the pool BEFORE resetting the primary, so a recycled buffer
            // doesn't carry a stale Next forward into the next test.
            CollapseChainToPrimary(slot);
            slot.Buffer?.Reset();
            // Re-anchor chain pointers (or null them out if the slot was never claimed and Buffer is still null).
            slot.ChainHead = slot.Buffer;
            slot.ChainTail = slot.Buffer;
        }
        SHighWaterMark = 0;
        SActiveSlotCount = 0;
        SGlobalCaptureActivityContext = true;

        // Clear the calling thread's TLS so the next GetOrAssignSlot from this thread will re-claim instead of returning a stale pre-reset index.
        // This handles the common case of a test fixture calling ResetForTests from its main thread between tests.
        var releaser = Releaser;
        if (releaser != null)
        {
            GC.SuppressFinalize(releaser);
        }
        SlotIndex = 0;
        Releaser = null;
    }

    /// <summary>
    /// Walk the slot's spillover chain forward from <see cref="ThreadSlot.Buffer"/>, returning every spillover ring to
    /// <see cref="SpilloverRingPool"/> and re-anchoring <see cref="ThreadSlot.ChainHead"/> / <see cref="ThreadSlot.ChainTail"/>
    /// to the primary. Safe to call when the slot is not claimed (no producer running). The primary itself is never
    /// recycled — only spillovers cycle through the pool.
    /// </summary>
    private static void CollapseChainToPrimary(ThreadSlot slot)
    {
        var primary = slot.Buffer;
        if (primary == null)
        {
            return;
        }
        var ring = primary.Next;
        primary.SetNext(null);
        while (ring != null)
        {
            var next = ring.Next;
            SpilloverRingPool.Release(ring);
            ring = next;
        }
        slot.ChainHead = primary;
        slot.ChainTail = primary;
    }

    /// <summary>
    /// Walk every claimed slot and collapse its chain back to the primary, releasing all spillovers to the pool.
    /// Called by <see cref="TyphonProfiler.Stop"/> before <see cref="SpilloverRingPool.Shutdown"/> so no spillover
    /// reference outlives the pool. Must run after producers have quiesced (consumer thread stopped).
    /// </summary>
    internal static void CollapseAllChainsToPrimary()
    {
        var hwm = SHighWaterMark;
        for (var i = 0; i < hwm; i++)
        {
            CollapseChainToPrimary(SSlots[i].Slot);
        }
    }

    /// <summary>
    /// Clears the current thread's slot claim so the next <see cref="GetOrAssignSlot"/> on this thread re-claims a slot. Tests only.
    /// </summary>
    internal static void ReleaseCurrentThreadForTests()
    {
        var releaser = Releaser;
        if (releaser == null)
        {
            return;
        }

        var idx = SlotIndex;
        if ((uint)idx < MaxSlots)
        {
            if (Interlocked.CompareExchange(ref SSlots[idx].State, (int)SlotState.Free, (int)SlotState.Active) == (int)SlotState.Active)
            {
                SSlots[idx].Slot.OwnerManagedThreadId = 0;
                SSlots[idx].Slot.OwnerOsThreadId = 0;
                SSlots[idx].Slot.OwnerThreadName = null;
                SSlots[idx].Slot.OwnerThreadKind = ThreadKind.Other;
                Interlocked.Decrement(ref SActiveSlotCount);
            }
        }

        SlotIndex = 0;
        Releaser = null;
        GC.SuppressFinalize(releaser);
    }
}
