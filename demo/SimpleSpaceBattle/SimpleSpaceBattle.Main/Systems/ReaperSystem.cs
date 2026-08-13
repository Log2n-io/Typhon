using Typhon.Engine;

namespace SimpleSpaceBattle;

/// <summary>
/// Phase <see cref="BattlePhases.Reap"/> — the only sequential system in the simulation.
///
/// <para>Merges the per-worker death buffers in ascending <c>WorkerId</c>, destroys them in one transaction, folds
/// the per-worker counters into the totals and decides whether the run has ended.</para>
///
/// <para><b>Sequential by necessity, not by omission.</b> Transactions have thread affinity, and CLUSTERWALK-01
/// forbids a cluster walk running concurrently with <c>Destroy</c> + <c>Commit</c> on the same archetype. Deferring
/// destruction to a phase where no walk is running is what lets the other three systems stay lock-free. The cost is
/// O(deaths), not O(N) — nothing here scans the fleet.</para>
/// </summary>
internal sealed class ReaperSystem : CallbackSystem
{
    private readonly BattleWorld _world;
    private readonly List<EntityId> _merged = new(4096);

    public ReaperSystem(BattleWorld world) => _world = world;

    protected override void Configure(SystemBuilder b) => b
        .Name("Reaper")
        .ShouldRun(() => !_world.IsTerminal)
        .Phase(BattlePhases.Reap)
        .Writes<VitalsComponent>()
        .WritesResource("RunState");

    protected override void Execute(TickContext ctx)
    {
        BattleWorld world = _world;
        world.CompletedTicks = (ulong)ctx.TickNumber;
        world.DrainWorkerLanes(_merged);

        // EVERY tick, destroys or not: cell migration can allocate new clusters, and a stale count makes the
        // parallel phases skip every cluster past it (see BattleWorld.RefreshClusterCount).
        world.RefreshClusterCount();

        if (_merged.Count > 0)
        {
            using (Transaction tx = world.Dbe.CreateQuickTransaction())
            {
                for (int i = 0; i < _merged.Count; i++)
                {
                    tx.Destroy(_merged[i]);
                }

                tx.Commit();
            }

            world.AliveCount -= _merged.Count;
            _merged.Clear();

            // Re-attach the shared snapshot only when destruction actually happened: it can invalidate a cached
            // chunk accessor, and Attach costs ~5 ms on this sequential path — 12 % of the tick budget — so paying
            // it on every quiet tick is pure Amdahl tax.
            world.ReattachAccessor();
        }

        if (world.AliveCount <= 1)
        {
            world.Outcome = world.AliveCount == 1 ? BattleOutcome.Winner : BattleOutcome.Draw;
        }
        else if (world.CompletedTicks >= world.Config.MaximumCompletedTicks)
        {
            world.Outcome = BattleOutcome.TimedOut;
        }
    }
}
