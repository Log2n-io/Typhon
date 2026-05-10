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
