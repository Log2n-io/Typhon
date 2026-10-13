using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Diagnostic hooks for OLC B+Tree descent paths. Tests/harnesses can wire <see cref="RecordStep"/>
/// to capture per-step state, and <see cref="OnInvalidChunkId"/> to dump captured state when the
/// page-cache rejects a bogus chunk-id (issue #297).
///
/// Production cost when unwired: one null-check per descent step + one null-check on the
/// invalid-chunk-id error path. The JIT short-circuits the null checks; no allocations.
///
/// LEAVE THIS CLASS WIRED UP IN TESTS ONLY. With all hooks null (default), the production
/// behavior is byte-for-byte identical to having no instrumentation.
/// </summary>
internal static class OlcDescentTrace
{
    /// <summary>
    /// Op codes for <see cref="RecordStep"/>. Differentiates which descent path emitted the step.
    /// </summary>
    public const int OpInsert = 0;
    public const int OpRemove = 1;
    public const int OpDescend = 2;  // OptimisticDescendToLeaf (used by lookups, Move, OLC insert general path)

    /// <summary>
    /// Called once per descent step, AFTER reading the child chunk-id from the parent and AFTER
    /// validating the parent's OLC version (so when called, the child chunk-id is the value the
    /// caller intends to use).
    /// </summary>
    public static Action<int, int, int, int, int> RecordStep;

    /// <summary>
    /// Called from <see cref="ChunkBasedSegment{TStore}.GetChunkLocation"/> right
    /// before it throws on an out-of-range chunk-id. Lets tests dump captured descent traces with
    /// a deterministic forensic record of how the bogus id propagated to the page-cache lookup.
    /// </summary>
    public static Action<int, string> OnInvalidChunkId;

    // === Remove NotFound branch instrumentation (issue #297 follow-up) ===

    /// <summary>Branch id for <see cref="OnRemoveNotFound"/>.</summary>
    public const int RemoveBranchBeginFastPathLessThanFirst = 1;
    public const int RemoveBranchEndFastPathGreaterThanLast = 2;
    public const int RemoveBranchGeneralKeyIndexNegative = 3;
    public const int RemoveBranchUnderLockReFindNegative = 4;

    // The PESSIMISTIC twins of branches 1 and 2. They were the blind spot: the stress harness saw Remove return false on a key that provably existed while
    // every branch counter read zero, which reads as "no not-found path was taken" and sent the hunt at the count instead of the lookup. Both conclusions live
    // in RemoveCorePessimistic's own begin/end fast paths, which duplicate the OLC logic and were never wired.
    public const int RemoveBranchPessimisticBeginLessThanFirst = 5;
    public const int RemoveBranchPessimisticEndGreaterThanLast = 6;

    /// <summary>One past the highest branch id — sizes a counter array without hard-coding the count at each call site.</summary>
    public const int RemoveBranchCount = 7;

    /// <summary>
    /// Called every time the OLC remove path concludes "key not in tree." Args:
    /// (branch, keyAsInt, leafChunkId, leafFirstOrLastKeyAsInt, leafCount). For non-int trees
    /// (e.g., String64) the int casts are nonsensical — wire only for int-keyed test trees.
    /// </summary>
    public static Action<int, int, int, int, int> OnRemoveNotFound;

    /// <summary>
    /// Fires in <c>InsertIterative</c> once the descent has completed and BEFORE any lock is taken, with (leafChunkId, ctx.Depth).
    /// </summary>
    /// <remarks>
    /// A seam, and the only one that makes IXW-05 deterministically testable. The window it opens is the defect's own: a completed descent holding NO lock,
    /// so the tree can grow a level underneath it and the version this writer reads next is already post-growth — which is precisely why the validation that
    /// follows cannot catch a stale path. Otherwise reachable only by a race measured at 1 in 7,162 root splits, so a test without this hook verifies the
    /// rule with probability near zero and would stay green if the guard were deleted. Null in production: one null-check per pessimistic descent.
    /// </remarks>
    public static Action<int, int> OnDescentComplete;

    /// <summary>
    /// Fires in <c>RemoveIterative</c> when a root collapse has been decided, BEFORE the swap, with the root's write lock held (#1329, IXW-10).
    /// </summary>
    /// <remarks>
    /// The seam that makes the collapse's latch testable: a writer parked here holds the root, so a split of the surviving child cannot publish its separator
    /// into the root until the collapse has completed. Done after the unlock, as it was, the split's separator landed in the root between the length test and
    /// the swap and was dropped. Null in production: one null-check per collapse.
    /// </remarks>
    public static Action OnRootCollapse;

    /// <summary>
    /// Fires when a pessimistic insert or remove has completed its structural work and released every latch, before its bookkeeping (#1329, IXW-10).
    /// </summary>
    /// <remarks>
    /// Where the tail pointer used to be repaired, lazily and with no latch. A check here sees the tail exactly as the split or the merge left it, so it
    /// proves the tail is maintained by the change itself. Null in production: one null-check per pessimistic write.
    /// </remarks>
    public static Action OnPessimisticWriteReturned;
}
