using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Cluster dormancy over a world whose creatures move only through <c>ClusterRef.WriteSpatial</c> (<c>--dormancy</c>, #896).
/// </summary>
/// <remarks>
/// <para>
/// <b>The demo marks nothing for dormancy's sake.</b> <c>CreatureMoveTick</c> used to mark a moved cluster's <see cref="Creature.Bounds"/> dirty, because
/// <c>WriteSpatial</c> raises no dirty bit and a cluster moved only through it used to look quiet to the fence's sleep sweep, sleep while walking, and
/// freeze. The engine now notes every <c>WriteSpatial</c> for the sweep and wakes a sleeping cluster it reaches (rules DM-01, DM-05), so the mark is gone,
/// and this is the check that a walking creature still walks.
/// </para>
/// <para>
/// <b>Two snapshots, both read inside a tick</b> (<see cref="TatooineSim.WriteInTick(System.Action{Transaction, long})"/>), five sleep thresholds apart. The
/// precondition is that dormancy is live in this world: a creature that is neither idle nor dead counts its think cooldown down on every tick its cluster
/// is dispatched, so one whose cooldown stood still for the whole window was in a cluster that slept.
/// </para>
/// </remarks>
[NonParallelizable]
internal sealed class CreatureDormancyChecks : ServedChecks
{
    private const int SleepTicks = 3;
    private const int WindowTicks = 5 * SleepTicks;

    private readonly record struct Seen(float X, float Z, int Mode, bool Moving, int ThinkCooldown, long MoveUntilTick);

    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void ACreatureWalkingALeg_KeepsWalking_WhileClustersAroundItSleep()
    {
        var config = Worlds.Served(Dir);
        config.DormancyTicks = SleepTicks;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Harness.Ticks(2 * WindowTicks);

        var before = new Dictionary<EntityId, Seen>();
        var from = sim.WriteInTick(tx => Read(tx, before));
        Harness.Ticks(WindowTicks);
        var after = new Dictionary<EntityId, Seen>();
        var to = sim.WriteInTick(tx => Read(tx, after));
        Assert.That(to - from, Is.GreaterThanOrEqualTo(WindowTicks), "precondition: the two snapshots are a window apart");

        var walkers = 0;
        var frozen = new List<string>();
        var slept = 0;
        foreach (var (id, a) in before)
        {
            if (!after.TryGetValue(id, out var b) || a.Mode is AiMode.Idle or AiMode.Dead || b.Mode == AiMode.Dead)
            {
                continue;
            }

            // A cooldown long enough to outlast the window, and not one tick of it counted: nothing dispatched this creature's cluster.
            if (a.ThinkCooldown > to - from && b.ThinkCooldown == a.ThinkCooldown)
            {
                slept++;
            }

            // Walking a leg that ends past the window, so it has no reason of its own to stop inside it.
            if (a.Mode == AiMode.Wander && a.Moving && a.MoveUntilTick > to)
            {
                walkers++;
                if (a.X == b.X && a.Z == b.Z)
                {
                    frozen.Add($"{id} at ({a.X:F1}, {a.Z:F1})");
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(slept, Is.GreaterThan(0), "precondition: no creature's cluster slept, so this world does not exercise dormancy");
            Assert.That(walkers, Is.GreaterThan(0), "precondition: no creature was walking a leg across the window");
            Assert.That(frozen, Is.Empty, $"{frozen.Count} of {walkers} walking creatures stood still for {to - from} ticks: their clusters slept while moving");
        });
    }

    private static void Read(Transaction tx, Dictionary<EntityId, Seen> into)
    {
        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            var places = cluster.GetReadOnlySpan(Creature.Bounds);
            var moves = cluster.GetReadOnlySpan(Creature.Move);
            var brains = cluster.GetReadOnlySpan(Creature.Ai);
            var timers = cluster.GetReadOnlySpan(Creature.Timers);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                var move = moves[slot];
                into[cluster.GetEntityId(slot)] = new Seen(places[slot].X, places[slot].Z, brains[slot].Mode, move.VelX != 0f || move.VelZ != 0f,
                    timers[slot].ThinkCooldown, timers[slot].MoveUntilTick);
            }
        }
    }
}
