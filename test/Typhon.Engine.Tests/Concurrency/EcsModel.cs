using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// A sequential reference model of the ECS operation set (#705 T5): what the engine's live population MUST look like after a set of operations, computed with
/// no engine, no locks and no storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model is the work</b>, and its value comes entirely from being independent. It is a multiset of live component values rather than a map keyed by
/// <c>EntityId</c>, because entity ids are assigned by the engine: a model that had to predict them would be re-implementing the allocator, and would then
/// agree with the engine about allocation for the same reason the engine agrees with itself. A multiset can be computed from the operation sequence alone.
/// </para>
/// <para>
/// <b>What that buys.</b> Every operation below is <i>commutative</i> on the multiset, so the expected final state is the same under every interleaving —
/// which is what makes a parallel run checkable at all without a happens-before graph. The properties it can then falsify are exactly the ones the concurrency
/// bucket is made of: an entity that another thread's write overwrote is a MISSING value (#708's colliding slots), a lost dirty mark is a missing value after
/// reopen (#400), and a duplicate is a value present twice.
/// </para>
/// <para>
/// <b>What it deliberately cannot see</b>: ordering between operations on the SAME entity, and anything about visibility mid-run. Those need a happens-before
/// model, which is a larger piece of work; stating the limit here keeps a green result from being read as more than it is.
/// </para>
/// </remarks>
internal sealed class EcsModel
{
    private readonly List<int> _live = [];

    /// <summary>Values expected to be live, in no particular order (a multiset — duplicates are meaningful).</summary>
    public IReadOnlyList<int> Live => _live;

    /// <summary>Offset applied by <see cref="SpawnThenUpdate"/>, so an entity that kept its spawn value is distinguishable from one that took the update.</summary>
    public const int UpdateOffset = 1_000_000;

    /// <summary>One entity spawned and left alive.</summary>
    public void Spawn(int value) => _live.Add(value);

    /// <summary>One entity spawned, then updated in a later transaction — only the updated value survives.</summary>
    public void SpawnThenUpdate(int value) => _live.Add(value + UpdateOffset);

    /// <summary>One entity spawned and then destroyed — net nothing, but the storage churn is real.</summary>
    public void SpawnAndDestroy(int value)
    {
        // Deliberately no-op on the multiset. Modelling it as add-then-remove would be identical and would only invite the reader to think ordering matters.
    }

    /// <summary>A read-only query. Changes nothing; present so the operation mix includes readers racing writers.</summary>
    public void Query()
    {
    }

    /// <summary>The live values, sorted — the canonical form both sides are compared in.</summary>
    public int[] Sorted()
    {
        var copy = _live.ToArray();
        System.Array.Sort(copy);
        return copy;
    }
}
