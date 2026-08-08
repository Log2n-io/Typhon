using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Optimistic Lock Coupling latch operating on a B+Tree node's OlcVersion field.
/// Layout (32 bits):
///   Bit 0:      Locked (exclusive writer active)
///   Bit 1:      Obsolete (node replaced by SMO)
///   Bits 2-31:  Version counter (30 bits, ~1.07B versions)
/// </summary>
internal readonly ref struct OlcLatch
{
    private readonly ref int _version;  // ref to chunk's OlcVersion field

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OlcLatch(ref int olcVersion) => _version = ref olcVersion;

    // --- Reader API (zero writes to shared state) ---

    /// <summary>
    /// Read version. Returns 0 if locked or obsolete (caller must restart).
    /// Acquire load: orders the caller's subsequent node reads after this version snapshot. Free on x64 (TSO — plain mov); emits ldar on arm64.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadVersion()
    {
        int v = Volatile.Read(ref _version);
        return (v & 0b11) == 0 ? v : 0;  // locked (bit 0) or obsolete (bit 1) -> restart
    }

    /// <summary>
    /// Validate version unchanged since snapshot. On mismatch, emit a Concurrency:OlcLatch:ValidationFail trace event (Tier-2 gated).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ValidateVersion(int expected)
    {
        // The caller's PLAIN data reads (node scan, value copy) sit between ReadVersion and this re-read. An acquire load only stops LATER accesses
        // from hoisting above it — it does NOT stop those earlier plain loads from sinking below the validating load on a weakly-ordered CPU, which
        // would void the validation. x64 (TSO) orders loads in program order, so the fence is needed only off-x86: X86Base.IsSupported is a JIT-time
        // constant, so this folds to nothing on x64 and emits the required barrier (dmb ish) on arm64.
        if (!X86Base.IsSupported)
        {
            Interlocked.MemoryBarrier();
        }

        var actual = Volatile.Read(ref _version);  // acquire: pairs with the release in WriteUnlock
        if (actual == expected)
        {
            return true;
        }
        TyphonEvent.EmitConcurrencyOlcLatchValidationFail((uint)expected, (uint)actual);
        return false;
    }

    /// <summary>
    /// After acquiring the write lock, validates that no other writer modified the node between our version snapshot and our lock acquisition.
    /// Must be called while holding the write lock.
    /// After TryWriteLock succeeds, _version = (v | 1) where v was read inside TryWriteLock.
    /// If v == expectedUnlockedVersion, nobody modified the node between our ReadVersion and our lock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ValidateVersionLocked(int expectedUnlockedVersion)
    {
        var actual = Volatile.Read(ref _version);  // acquire: pairs with the release in WriteUnlock
        var expectedLocked = expectedUnlockedVersion | 1;
        if (actual == expectedLocked)
        {
            return true;
        }
        TyphonEvent.EmitConcurrencyOlcLatchValidationFail((uint)expectedLocked, (uint)actual);
        return false;
    }

    // --- Writer API ---

    /// <summary>
    /// Acquire exclusive write lock. Returns false when the node is locked by another writer OR obsolete; emits a Concurrency:OlcLatch:WriteLockAttempt trace
    /// event on failure. Callers distinguish the two with <see cref="IsObsolete"/>: locked is transient (spin or restart), obsolete is permanent (restart only).
    /// </summary>
    /// <remarks>
    /// Issue #716 (rule IXW-02). This used to gate on the locked bit alone, which made a node a concurrent merge had already detached a legal write target. The
    /// OLC fast paths survived that by accident: they follow the lock with <see cref="ValidateVersionLocked"/>, and since <see cref="MarkObsolete"/> sets a bit
    /// INSIDE the version word, the comparison fails and the operation restarts. Paths that re-check business conditions instead of the version — a leaf with
    /// room, no right sibling, keys in order — got no such protection, and a detached node satisfies all three. The insert then lands in a node unreachable from
    /// the root: the key is silently lost, which is #297's and #679's exact symptom.
    /// <para>
    /// Refusing here makes the invariant STRUCTURAL rather than something each of seventeen call sites has to remember. <see cref="MarkObsolete"/> requires the
    /// write lock, so a node that is not obsolete at the instant of this CAS cannot become obsolete while the acquisition holds — the check is not merely a
    /// hint. The one deliberate exception is <see cref="TryWriteLockOnSmoPath"/>; see its remarks for why it exists and what bounds it.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteLock()
    {
        int v = _version;
        if ((v & 0b11) != 0)  // locked (bit 0) or obsolete (bit 1)
        {
            TyphonEvent.EmitConcurrencyOlcLatchWriteLockAttempt((uint)v, false);
            return false;
        }
        if (Interlocked.CompareExchange(ref _version, v | 0b1, v) == v)
        {
            return true;
        }
        TyphonEvent.EmitConcurrencyOlcLatchWriteLockAttempt((uint)v, false);
        return false;
    }

    /// <summary>
    /// Acquire exclusive write lock, admitting an obsolete node. Reports through <paramref name="wasObsolete"/> whether the acquired node was already detached.
    /// </summary>
    /// <remarks>
    /// The four latch-coupled SMO sibling sites (split spill in <c>InsertIterative</c> Phase 3, borrow/merge in <c>RemoveIterative</c> Phase 3) are mid-algorithm
    /// with no restart point: the leaf mutation has already happened and the promoted key or the merge MUST be propagated. They also cannot simply skip a sibling
    /// — <c>HandleChildMerge</c> resolves it a second time internally and its merge branch dereferences it, so dropping it would trade a rare lost key for a
    /// certain null dereference.
    /// <para>
    /// What bounds them instead is that both phases hold the write lock on the sibling's parent, version-validated against the descent, so no merge can be
    /// detaching a TRUE sibling underneath them. A COUSIN (the left/right edge case, whose parent is a different node this operation does not hold) is not
    /// covered by that argument, which is why the outcome is reported and counted (<c>BTree.ObsoleteSmoSiblingLocks</c>) rather than assumed away. Measured over
    /// the full gate suite the counter reads 0; if it ever does not, the residual is real and the fix is to make the sibling droppable, which needs
    /// <c>NodeRelatives.HasTrue*Sibling</c> to become mutable.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteLockOnSmoPath(out bool wasObsolete)
    {
        int v = _version;
        if ((v & 0b1) != 0)
        {
            wasObsolete = false;
            TyphonEvent.EmitConcurrencyOlcLatchWriteLockAttempt((uint)v, false);
            return false;
        }
        if (Interlocked.CompareExchange(ref _version, v | 0b1, v) == v)
        {
            wasObsolete = (v & 0b10) != 0;
            return true;
        }
        wasObsolete = false;
        TyphonEvent.EmitConcurrencyOlcLatchWriteLockAttempt((uint)v, false);
        return false;
    }

    /// <summary>
    /// Release write lock and increment version.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnlock()
    {
        // Increment version (bits 2-31), clear locked (bit 0), preserve obsolete (bit 1)
        // Release store: publishes every write made under the lock before the unlock is visible. Free on x64 (TSO — plain mov); emits stlr on arm64.
        int v = _version;
        var newV = ((v >> 2) + 1) << 2 | (v & 0b10);  // version++, keep obsolete, clear lock
        Volatile.Write(ref _version, newV);
        TyphonEvent.EmitConcurrencyOlcLatchWriteUnlock((uint)v, (uint)newV);
    }

    /// <summary>
    /// Mark node as obsolete. Must hold write lock.
    /// </summary>
    /// <remarks>
    /// Release store, not a plain one. The write lock serialises WRITERS, so the read-modify-write needs no interlock — but readers observe this word with
    /// <c>Volatile.Read</c> outside the lock, and since #716 <see cref="TryWriteLock"/> refuses on this bit, a writer's acquisition decision now depends on it
    /// too. On arm64 a plain store may be observed after stores the merge made before it; publishing with release keeps the bit ordered behind them.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkObsolete()
    {
        var v = _version | 0b10;
        Volatile.Write(ref _version, v);
        TyphonEvent.EmitConcurrencyOlcLatchMarkObsolete((uint)v);
    }

    /// <summary>
    /// Release write lock WITHOUT incrementing version. Used when a writer acquires the lock but decides to restart without modifying the
    /// node (e.g., version validation failure).
    /// This avoids unnecessary version bumps that would cause cascading restarts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AbortWriteLock() => Volatile.Write(ref _version, _version & ~0b1);  // Release-clear locked bit (bit 0), leaving version counter and obsolete bit unchanged

    /// <summary>Check if locked (for diagnostics only).</summary>
    public bool IsLocked => (_version & 0b1) != 0;

    /// <summary>Check if obsolete.</summary>
    public bool IsObsolete => (_version & 0b10) != 0;
}
