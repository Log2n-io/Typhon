// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

// unset

using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

[PublicAPI]
public delegate void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver);

internal ref struct CommitContext
{
    public long PrimaryKey;
    public ComponentInfo Info;
    public ref ComponentInfo.CompRevInfo CompRevInfo;
    public ConcurrencyConflictSolver Solver;
    public ConcurrencyConflictHandler Handler;
    public ref UnitOfWorkContext Ctx;

    // Hoisted from per-entity to per-commit: determined once before the entity loop
    public bool IsTail;
    public long NextMinTSN;  // Valid when IsTail == true: the TSN to keep revisions for
    public long TailTSN;     // Valid when IsTail == false: the blocking tail's TSN
}
