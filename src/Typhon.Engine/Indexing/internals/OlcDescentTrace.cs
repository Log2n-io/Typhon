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
}
