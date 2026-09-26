using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The S0 gate of <c>03-server.md §10</c>: the simulation defects that made every published measurement describe a world other than the one intended.
/// </summary>
/// <remarks>
/// These are not unit tests of a method; they are the preconditions a published figure rests on, and each one failed silently for weeks because nothing
/// asserted it. A sweep run against a red fixture here measures the wrong world, which is exactly what happened to the partitioning conclusions in
/// <c>claude/design/Spatial/swg-tatooine-workload.md</c>.
/// </remarks>
internal static class S0
{
    /// <summary>Every creature's position and lair, read through the realm's spatial index and then the entity itself.</summary>
    public static List<(long Key, float X, float Z, float HomeX, float HomeZ, int Mode)> Creatures(TatooineSim sim)
    {
        var rows = new List<(long, float, float, float, float, int)>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[64];
        var e = sim.Dbe.ClusterSpatialQuery<Creature>(RealmId.Default).AABB(in Worlds.Everywhere);
        try
        {
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                for (var i = 0; i < got; i++)
                {
                    var id = buffer[i].Entity;
                    var entity = tx.Open(id);
                    var bounds = entity.Read(Creature.Bounds);
                    var ai = entity.Read(Creature.Ai);
                    rows.Add((id.EntityKey, bounds.X, bounds.Z, ai.HomeX, ai.HomeZ, ai.Mode));
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        rows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return rows;
    }

    /// <summary>A world's behavioural fingerprint: every creature's key and where it ended up, to the millimetre.</summary>
    public static string Fingerprint(TatooineSim sim)
    {
        var rows = Creatures(sim);
        var h = new System.Text.StringBuilder(rows.Count * 12);
        foreach (var r in rows)
        {
            h.Append(r.Key).Append(':').Append((int)MathF.Round(r.X * 1000f)).Append(',').Append((int)MathF.Round(r.Z * 1000f)).Append(';');
        }

        return h.ToString();
    }
}

/// <summary>
/// S0-1: a wandering creature must be spread around its lair, not sitting on it. Acceptance M0-1.
/// </summary>
/// <remarks>
/// The defect this guards: revival used to signal itself by setting <c>ThinkCooldown</c> to 1, and the move system read that as "just revived" and teleported
/// the creature home — so every living wanderer was snapped to its lair's exact centre once per AI cycle. Measured before the fix: 96 % of wanderers within
/// 3 m of the centre, 1 278 of them at exactly 0 m. A world of point clusters is the one thing a cell-size sweep must not be measured on.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class CreatureDispersionTests
{
    private string _dir;
    private TatooineSim _sim;

    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.TickRateHz = 10;
        config.WarmTicks = 10;

        // 240 ticks at 10 Hz is 24 s — four wander cycles, enough for a creature to have walked several legs away from home.
        config.MeasuredTicks = 240;
        config.WorkerCount = 1;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        _sim.Run();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    [Test]
    public void WanderingCreatures_AreSpreadAroundTheirLair_NotSittingOnIt()
    {
        var rows = S0.Creatures(_sim);
        var wanderers = 0;
        var atCentre = 0;
        var exactlyHome = 0;
        foreach (var r in rows)
        {
            // Only wanderers: an Idle creature never moves by design, and a Dead one is waiting to revive.
            if (r.Mode != AiMode.Wander)
            {
                continue;
            }

            wanderers++;
            var dx = r.X - r.HomeX;
            var dz = r.Z - r.HomeZ;
            var d2 = (dx * dx) + (dz * dz);
            if (d2 < 9f)
            {
                atCentre++;
            }

            if (d2 == 0f)
            {
                exactlyHome++;
            }
        }

        Assert.That(wanderers, Is.GreaterThan(100), "the world has a wandering creature population to measure");

        // The failure mode was 96 %. M0-1 asks for under 5 %.
        var share = atCentre / (double)wanderers;
        Assert.That(share, Is.LessThan(0.05), $"{atCentre} of {wanderers} wanderers sit within 3 m of their lair centre ({share:P1})");
        Assert.That(exactlyHome, Is.Zero, "no wanderer sits at exactly its lair centre, which only a teleport can produce");
    }
}

/// <summary>
/// S0-1, the other half: a creature that really does revive is teleported home exactly once, and the flag that says so is consumed.
/// </summary>
/// <remarks>
/// <see cref="CreatureDispersionTests"/> proves the teleport no longer fires for a living wanderer, but at the shipped 180 s respawn nothing revives inside a
/// short run, so it never exercises a real revival at all. A flag that were raised and never cleared would teleport its creature home on every tick for the
/// rest of the run — pinned to a point, invisible in the aggregate, and the same corruption of the workload that S0-1 was.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class CreatureRevivalTests
{
    private string _dir;
    private TatooineSim _sim;

    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.TickRateHz = 10;
        config.WarmTicks = 0;
        config.MeasuredTicks = 200;
        config.WorkerCount = 1;
        config.Unpaced = false;

        // Full population: at 5 % there are too few players for any of them to meet a creature, and the run kills nothing to revive.
        config.PopulationScale = 1f;

        // 3 s instead of 180: revivals have to happen inside the window for this to test anything.
        config.RespawnSeconds = 3f;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        _sim.Run();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    [Test]
    public void CreaturesRevive_AndNoReviveFlagIsLeftUnconsumed()
    {
        Assert.That(_sim.LastStats.CreaturesKilled, Is.GreaterThan(0), "players killed creatures, so there is something to revive");
        Assert.That(_sim.LastStats.CreaturesRespawned, Is.GreaterThan(0), "and the revival path ran inside the measured window");

        var stuck = 0;
        using var tx = _sim.Dbe.CreateQuickTransaction();
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[64];
        var e = _sim.Dbe.ClusterSpatialQuery<Creature>(RealmId.Default).AABB(in Worlds.Everywhere);
        try
        {
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                for (var i = 0; i < got; i++)
                {
                    if (tx.Open(buffer[i].Entity).Read(Creature.Timers).JustRevived != 0)
                    {
                        stuck++;
                    }
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        // Not zero, and the reason is the phase order: combat raises the flag in Resolve, and Move — which consumes it — runs EARLIER in the tick, so a
        // creature revived on tick N is teleported on tick N + 1. Whatever revived on the run's last tick still carries the flag, legitimately.
        // What falsifies a flag that is never consumed is the RATIO: unconsumed would equal revived, not trail it by a tick's worth.
        var revived = _sim.LastStats.CreaturesRespawned;
        Assert.That(stuck * 10L, Is.LessThan(revived), $"{stuck} of {revived} revivals left JustRevived set — a tick's residue is expected, a tenth is not");
    }
}

/// <summary>
/// S0-4: the same <c>--seed</c> reproduces the same run, and a different one does not. Acceptance M0-4.
/// </summary>
/// <remarks>
/// Per-tick randomness used to be salted with the cluster id and the slot — where an entity happens to be stored — and never with the seed. So
/// <c>--seed</c> reached world generation only, two runs of the same seed could diverge, and a repair pass or a cross-realm migration silently changed every
/// draw. The negative arm is the one that matters: before the fix, changing the seed changed nothing about per-tick behaviour.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class SimulationDeterminismTests
{
    private static string RunFingerprint(int seed)
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.TickRateHz = 10;
            config.WarmTicks = 0;
            config.MeasuredTicks = 20;

            // One worker: M0-4's shape. Chunk order across workers is not part of the contract, per-entity draws are.
            config.WorkerCount = 1;
            config.Seed = seed;

            // PACED, and that is load-bearing. Unpaced, the run loop polls every 5 ms and calls Shutdown after the count is reached, so an indeterminate
            // number of extra ticks execute first and two runs end at different tick numbers — measured here: divergence from the first tick, world
            // generation itself identical. Reproducing an unpaced run needs a tick limit the runtime does not offer. Paced, the stop is exact and the
            // fingerprints match to the millimetre.
            config.Unpaced = false;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            sim.Run();
            return S0.Fingerprint(sim);
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    [Test]
    public void SameSeed_ReproducesTheRun_AndADifferentSeedDoesNot()
    {
        var a = RunFingerprint(12345);
        var b = RunFingerprint(12345);
        var c = RunFingerprint(999);

        Assert.That(a, Is.Not.Empty, "the fingerprint covers a creature population");
        Assert.That(b, Is.EqualTo(a), "the same seed must reproduce the same run");
        Assert.That(c, Is.Not.EqualTo(a), "a different seed must reach the per-tick draws, not only world generation");
    }
}
