namespace Typhon.Engine.Internals;

/// <summary>
/// Reason codes for a no-progress return from <c>InsertIterative</c> or <c>RemoveIterative</c> — every place either gives up and asks its caller to
/// re-descend. One table, because the five descent checks are literally shared code and a second table would let the two drift.
/// </summary>
/// <remarks>
/// #738 exists because all seventeen collapsed into one counter. <c>AddOrUpdateCorePessimistic</c>'s retry loop increments a single restart tally and bounds
/// on <c>MaxPessimisticRestarts</c>, so a nightly record of a restart storm said how MANY times the operation gave up and nothing about WHERE — and the fix
/// for each of these is different. Two of the seventeen were counted before this table existed, both into <c>ObsoleteRestarts</c>, which the stress harness
/// did not even sample.
/// <para>
/// The four <c>*LockFailed</c> codes matter most, because they are invisible in every other instrument. <see cref="OlcLatch.TryWriteLock"/> does not spin and
/// never touches <c>_writeLockFailures</c> — that counter tallies <c>SpinWriteLock</c> iterations only. So a convoy on a shared ancestor produces a record
/// reading <c>WriteLockFails +0</c>, which is easily misread as "no lock contention" when it means "nobody spun". The 2026-08-31 nightly is exactly that
/// record.
/// </para>
/// <para>
/// Ordered root → leaf, following the method. <see cref="Unknown"/> is the initial value and must never be counted: a non-zero tally there means a bail was
/// added without a reason code, which <c>BTreeRetryExitInstrumentationTests</c> asserts against.
/// </para>
/// </remarks>
internal static class InsertRetryExit
{
    /// <summary>Initial value — a bail that forgot to name itself. Always 0 in a correct build.</summary>
    public const int Unknown = 0;

    // --- Phase 1, inside the shared DescendRecordingPath. Split four ways because the single "the descent failed" code measured 92% of the histogram on its
    // first run, which names a method rather than a cause. The four are different events: a locked node, a stale hop, a concurrent modification, and a read
    // that should be impossible.

    /// <summary>A node on the path is write-LOCKED. Transient: the holder will finish. The descent restarts from the root rather than waiting.</summary>
    public const int DescentNodeLocked = 1;

    /// <summary>A node on the path is OBSOLETE — detached by an SMO and never coming back. Re-descending is the only correct response (IXS-03).</summary>
    public const int DescentNodeObsolete = 2;

    /// <summary>The parent's second validation failed: an SMO completed between reading the child pointer and taking the child's version (IXS-07).</summary>
    public const int DescentParentRevalidateFailed = 3;

    /// <summary>A node was modified between its version read and the data read taken under it — the plain OLC restart.</summary>
    public const int DescentNodeVersionChanged = 4;

    /// <summary>
    /// A validated read still yielded an invalid child pointer. Should be unreachable; non-zero means the version check is not covering the read (#297).
    /// </summary>
    public const int DescentChildInvalid = 5;

    /// <summary>The leaf's version read 0 — locked by another writer (transient; obsolete is reported as <see cref="LeafObsolete"/>).</summary>
    public const int LeafVersionZero = 6;

    /// <summary>The leaf turned obsolete while its write lock was being taken (IXW-02, #716).</summary>
    public const int LeafObsolete = 7;

    /// <summary>The leaf's version changed between the descent and the lock — somebody else modified it first.</summary>
    public const int LeafVersionChanged = 8;

    /// <summary>The B-link right-walk reached a leaf a concurrent merge had detached (#716).</summary>
    public const int MoveRightNextObsolete = 9;

    /// <summary>The right-walk found a key-space gap: the key belongs to neither leaf, so a split's separator has not propagated yet (#297).</summary>
    public const int MoveRightGap = 10;

    /// <summary>The key sits below the leaf's lower bound — the previous leaf's HighKey (IXW-03, #740).</summary>
    public const int KeyBelowLowerBound = 11;

    /// <summary>
    /// The right-walk landed on a FULL leaf, whose recorded path belongs to the leaf the descent started from, so it cannot be split here (#679).
    /// </summary>
    /// <remarks>
    /// Structurally the most suspect of the table: it converges only when someone else splits that leaf or the separator propagates, and this operation does
    /// neither. It is the concrete shape of the crabbing fallback that <c>claude/design/Indexing/latch-coupled-smo.md</c> §6 specifies and nobody wrote.
    /// </remarks>
    public const int MovedRightLeafFull = 12;

    /// <summary>Could not lock the left leaf neighbour needed for a spill. <see cref="OlcLatch.TryWriteLock"/>, so invisible in <c>WriteLockFailures</c>.
    /// </summary>
    public const int LeafPrevLockFailed = 13;

    /// <summary>Could not lock the right leaf neighbour. <see cref="OlcLatch.TryWriteLock"/>, so invisible in <c>WriteLockFailures</c>.</summary>
    public const int LeafNextLockFailed = 14;

    /// <summary>
    /// Could not lock an ancestor on the recorded path. <see cref="OlcLatch.TryWriteLock"/>, so invisible in <c>WriteLockFailures</c>.
    /// </summary>
    /// <remarks>
    /// The one that scales with writer count: every writer's path ends at the same root, so N writers splitting anywhere contend here. On an oversubscribed box
    /// the holder can be descheduled for milliseconds while the rest burn retries, which is a convoy rather than a version-invalidation storm and wants a
    /// different fix from the rest of this table.
    /// </remarks>
    public const int PathLockFailed = 15;

    /// <summary>An ancestor's version changed between the descent and the path lock.</summary>
    public const int PathVersionChanged = 16;

    /// <summary>
    /// The descent recorded no internal path — it saw the root as a leaf — but the tree has since grown a level above that leaf (#738).
    /// </summary>
    /// <remarks>
    /// Restarting here is not contention handling, it is the guard on a structural precondition. With <c>Depth == 0</c> the insert's Phase 4 builds a
    /// NEW ROOT over the leaf it holds, and that is only correct while the leaf still IS the root. The leaf's own version does not establish that:
    /// it proves the leaf was not modified, not that no level appeared above it. Without this check Phase 4 runs <c>SetLeft(Root)</c> against a Root
    /// that is now an internal node while promoting a leaf, producing a root whose two children sit at different levels — measured at 1 in 7,162
    /// root splits, and every unroutable-leaf stall traced back to one.
    /// </remarks>
    public const int RootMovedUnderDescent = 17;

    // --- The REMOVE path's own bails. The five Descent* codes above are SHARED: DescendRecordingPath serves both write paths and reports the same five
    // checks to each. Everything below is RemoveIterative's.
    //
    // Added because the counter split left Remove behind, and that asymmetry cost real time: after the insert path was instrumented, every stall the 4-core
    // stress reproduction produced was the REMOVE loop burning its 10,000-retry bound — on the one path with no per-exit histogram, so the record said only
    // that it had happened. That is exactly the state #738 spent months in.

    /// <summary>The leaf's version read 0 — locked by another writer, or obsolete (IXS-03 keeps those apart; this bail cannot).</summary>
    public const int RemoveLeafVersionZero = 18;

    /// <summary>The leaf turned obsolete while its write lock was being taken — a merge detached it (IXW-02, #716).</summary>
    public const int RemoveLeafObsolete = 19;

    /// <summary>The leaf's version changed between the descent and the lock.</summary>
    public const int RemoveLeafVersionChanged = 20;

    /// <summary>
    /// The key is at or past this leaf's HighKey: a concurrent split's separator has not propagated, so the descent landed one leaf short (#297).
    /// </summary>
    public const int RemoveStaleSeparator = 21;

    /// <summary>The leaf is not authoritative for the key — below its lower bound (IXW-03), or empty but still chained.</summary>
    public const int RemoveLeafNotAuthoritative = 22;

    /// <summary>
    /// Could not lock the left leaf neighbour needed for a borrow or merge. <see cref="OlcLatch.TryWriteLock"/>, so invisible in <c>WriteLockFailures</c>.
    /// </summary>
    public const int RemoveLeafPrevLockFailed = 23;

    /// <summary>Could not lock the right leaf neighbour. <see cref="OlcLatch.TryWriteLock"/>, so invisible in <c>WriteLockFailures</c>.</summary>
    public const int RemoveLeafNextLockFailed = 24;

    /// <summary>Could not lock an ancestor on the recorded path. Scales with writer count — every path ends at the same root.</summary>
    public const int RemovePathLockFailed = 25;

    /// <summary>An ancestor's version changed between the descent and the path lock.</summary>
    public const int RemovePathVersionChanged = 26;

    /// <summary>
    /// The insert reached its split holding every latch it needs, but had not reserved the nodes the split allocates (IXW-07): the leaf filled after the
    /// descent looked at it, or the tree grew a level. Released everything; the next pass reserves before it latches. An INSERT code, after Remove's.
    /// </summary>
    public const int SplitReservationShort = 27;

    /// <summary>One past the highest code — sizes the counter array without hard-coding the count at each call site.</summary>
    public const int Count = 28;

    /// <summary>Short names, indexed by code, for diagnostic dumps.</summary>
    public static readonly string[] Names =
    [
        "Unknown",
        "DescentNodeLocked",
        "DescentNodeObsolete",
        "DescentParentRevalidateFailed",
        "DescentNodeVersionChanged",
        "DescentChildInvalid",
        "LeafVersionZero",
        "LeafObsolete",
        "LeafVersionChanged",
        "MoveRightNextObsolete",
        "MoveRightGap",
        "KeyBelowLowerBound",
        "MovedRightLeafFull",
        "LeafPrevLockFailed",
        "LeafNextLockFailed",
        "PathLockFailed",
        "PathVersionChanged",
        "RootMovedUnderDescent",
        "RemoveLeafVersionZero",
        "RemoveLeafObsolete",
        "RemoveLeafVersionChanged",
        "RemoveStaleSeparator",
        "RemoveLeafNotAuthoritative",
        "RemoveLeafPrevLockFailed",
        "RemoveLeafNextLockFailed",
        "RemovePathLockFailed",
        "RemovePathVersionChanged",
        "SplitReservationShort",
    ];
}
