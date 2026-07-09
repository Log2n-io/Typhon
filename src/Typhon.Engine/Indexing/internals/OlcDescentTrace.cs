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

    /// <summary>
    /// Called every time the OLC remove path concludes "key not in tree." Args:
    /// (branch, keyAsInt, leafChunkId, leafFirstOrLastKeyAsInt, leafCount). For non-int trees
    /// (e.g., String64) the int casts are nonsensical — wire only for int-keyed test trees.
    /// </summary>
    public static Action<int, int, int, int, int> OnRemoveNotFound;
}
