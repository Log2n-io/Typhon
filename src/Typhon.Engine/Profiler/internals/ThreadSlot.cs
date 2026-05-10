using System.Runtime.InteropServices;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-owned bookkeeping for one <see cref="ThreadSlotRegistry"/> slot: the variable-size SPSC ring the owning thread writes to, plus the
/// per-thread Activity-context opt-out flag and monotonic span counter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership split:</b> this class holds <i>producer-only</i> fields. The consumer-written lifecycle state (<see cref="SlotState"/>) lives on
/// the <see cref="PaddedSlot"/> struct in the registry's backing array, on a separate cache line. Keeping them on distinct cache lines means the
/// consumer's state CAS on every drain tick does not invalidate the producer's cached <see cref="Buffer"/> pointer.
/// </para>
/// <para>
/// <b>Lazy buffer allocation:</b> <see cref="Buffer"/> is <c>null</c> until <see cref="ThreadSlotRegistry.ClaimSlot"/> assigns a real
/// <see cref="TraceRecordRing"/>. Only the ~30 typical active slots consume event-buffer memory; the other 226 slots cost nothing at steady state.
/// </para>
/// </remarks>
internal sealed class ThreadSlot
{
    /// <summary>
    /// <b>Primary</b> variable-size SPSC ring buffer owned by this slot. <c>null</c> until the first claim assigns one; reused across re-claims
    /// of the same slot. Always the head of the per-slot chain — see <see cref="ChainHead"/> / <see cref="ChainTail"/> for the spillover model.
    /// Never recycled to <see cref="SpilloverRingPool"/>; only spillover rings cycle through there.
    /// </summary>
    public TraceRecordRing Buffer;

    /// <summary>
    /// Consumer's drain start: the oldest live ring in this slot's chain. Initialised to <see cref="Buffer"/> on first claim and on every
    /// re-claim. Advances forward (along <see cref="TraceRecordRing.Next"/>) when the consumer fully drains the current head and observes a
    /// successor — the previous head, if it was a spillover, is recycled to the pool. Always points at a live ring once the slot has been
    /// claimed; never null.
    /// </summary>
    public TraceRecordRing ChainHead;

    /// <summary>
    /// Producer's write target: the newest ring in this slot's chain. Initialised to <see cref="Buffer"/> on first claim and on every re-claim.
    /// Advances forward when the producer overflows the current tail and acquires a fresh spillover from
    /// <see cref="SpilloverRingPool"/>. Once a ring becomes a non-tail link it is drained-only: the producer never writes back to it. Always
    /// points at a live ring once the slot has been claimed; never null.
    /// </summary>
    public TraceRecordRing ChainTail;

    /// <summary>
    /// Managed thread ID of the current owner, or 0 if the slot is free. Diagnostic only — never used as an index because the CLR recycles
    /// managed thread IDs.
    /// </summary>
    public int OwnerManagedThreadId;

    /// <summary>
    /// Name of the current owning thread, captured once at claim time. Cached so exporters can synthesize catch-up
    /// <see cref="TraceEventKind.ThreadInfo"/> records for clients that connect after the slot was claimed — without this, mid-session
    /// live connections have no slot→name mapping and the viewer can't label lanes. Nulled on retirement. Not read on the hot path.
    /// </summary>
    public string OwnerThreadName;

    /// <summary>
    /// Category of the current owning thread (Main / Worker / Pool / Other), captured at claim time. Cached so the
    /// TCP catch-up replay can synthesize ThreadInfo records that include the kind for clients that connect after
    /// the slot was claimed. Reset to <see cref="ThreadKind.Other"/> on retirement.
    /// </summary>
    public ThreadKind OwnerThreadKind;

    /// <summary>
    /// Hot-path flag: when <c>false</c>, <c>TyphonEvent</c> skips the <see cref="System.Diagnostics.Activity.Current"/> lookup entirely,
    /// saving ~5–9 ns per span. Cleared via <c>TyphonEvent.SuppressActivityContextOnThisThread</c> by scheduler workers and the profiler consumer
    /// thread at their startup entry.
    /// </summary>
    public bool CaptureActivityContext;

    /// <summary>
    /// Per-slot monotonic span counter. Producer-only-written via plain <c>++</c> (single writer ⇒ no <see cref="System.Threading.Interlocked"/>
    /// needed). NEVER reset on claim or reclaim — successive owners of the same slot keep the counter monotonic, so the <c>(slot, counter)</c>
    /// pair is unique for the lifetime of the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SpanId uniqueness:</b> <c>SpanId = ((ulong)slotIdx &lt;&lt; 56) | counter</c>. Slot index disambiguates concurrent owners of distinct
    /// slots; the never-reset counter disambiguates successive owners of the same slot. 56-bit counter space ≈ 2280 years at 1M spans/sec.
    /// </para>
    /// </remarks>
    public long SpanCounter;

    public ThreadSlot()
    {
        CaptureActivityContext = true;
    }
}

/// <summary>
/// Cache-line-padded entry in <c>ThreadSlotRegistry.s_slots</c>. Holds the consumer-written lifecycle <see cref="State"/> and the reference to
/// the <see cref="ThreadSlot"/> heap object. One struct per slot, exactly 64 bytes so adjacent slots do not false-share.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct PaddedSlot
{
    /// <summary>Lifecycle state — CAS target. Values correspond to <see cref="SlotState"/>.</summary>
    [FieldOffset(0)]
    public int State;

    /// <summary>Reference to the producer-owned bookkeeping. Allocated lazily by <c>ThreadSlotRegistry.ClaimSlot</c>.</summary>
    [FieldOffset(8)]
    public ThreadSlot Slot;
}
