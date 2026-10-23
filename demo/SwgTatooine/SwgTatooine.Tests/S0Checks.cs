using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Server;
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

    /// <summary>
    /// Every pooled player's key, position, activity and destination — the behavioural fingerprint of <c>PlayerThink</c>.
    /// </summary>
    /// <param name="sim">A run that has finished.</param>
    /// <returns>The fingerprint.</returns>
    /// <remarks>
    /// <para>
    /// <b><see cref="Fingerprint"/> covers <c>Creature</c> and nothing else, so it is blind to every line of <c>PlayerThinkTick</c>.</b> That matters now
    /// because B2/B3 extract the player brain into <c>SwgTatooine.Brain</c> and rewire <c>PlayerThink</c> to call it, a refactor whose whole contract is
    /// "behaviour must not change" — and the only thing that can hold a refactor to that is a fingerprint taken before it. There was none for players.
    /// </para>
    /// <para>
    /// <b>The activity and the destination are in it, not just the position.</b> A player's position alone would miss a brain that picked a different goal
    /// and happened to be walking through the same place this tick; <c>Activity</c>, <c>ActivityTicks</c> and the destination are the goal FSM's own state,
    /// which is the thing being moved into another assembly. Rounded to the millimetre for the same reason the creature fingerprint is: the float arithmetic
    /// is identical run to run, and a millimetre is finer than any behavioural difference could be.
    /// </para>
    /// <para>
    /// <see cref="WorldIndex.Players"/> holds the pooled crowd the world builder spawned, which is exactly the set <c>PlayerThink</c> drives — an avatar
    /// spawned by a login is not in it, and must not be, because a session drives that one.
    /// </para>
    /// </remarks>
    public static string PlayerFingerprint(TatooineSim sim)
    {
        var ids = sim.Index.Players;
        var rows = new List<(long Key, int X, int Z, int Activity, int Ticks, int DestX, int DestZ)>(ids.Count);

        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            var players = tx.For<Player>();
            for (var i = 0; i < ids.Count; i++)
            {
                if (!players.TryOpen(ids[i], out var player))
                {
                    continue;   // retired by a clone or a realm teardown; its absence is part of the fingerprint through the key that is missing
                }

                var place = player.Read(Player.Bounds);
                var state = player.Read(Player.State);
                var move = player.Read(Player.Move);
                rows.Add((
                    ids[i].EntityKey,
                    (int)MathF.Round(place.X * 1000f),
                    (int)MathF.Round(place.Z * 1000f),
                    (int)state.Activity,
                    state.ActivityTicks,
                    (int)MathF.Round(move.DestX * 1000f),
                    (int)MathF.Round(move.DestZ * 1000f)));
            }
        }

        // By key, because the index's order is the world builder's and a later change to it would read as a behavioural difference.
        rows.Sort((a, b) => a.Key.CompareTo(b.Key));

        var h = new System.Text.StringBuilder(rows.Count * 24);
        foreach (var r in rows)
        {
            h.Append(r.Key).Append(':').Append(r.X).Append(',').Append(r.Z)
                .Append('/').Append(r.Activity).Append('+').Append(r.Ticks)
                .Append('>').Append(r.DestX).Append(',').Append(r.DestZ).Append(';');
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
[Category("Integration")]
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
[Category("Integration")]
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

        // Unpaced: every interval is in seconds converted at the configured rate, so 200 ticks are 20 s of world either way, and paced they were 20 s of
        // wall clock. Nothing here needs an exact stop. Measured unpaced: 63 kills, 55 revivals, two seconds.
        config.Unpaced = true;

        // Full population: at 5 % there are too few players for any of them to meet a creature, and the run kills nothing to revive. At 40 % it revives six,
        // which is too few for the one-in-ten ratio asserted below to mean anything.
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

            // Unpaced, which an exact stop makes safe. The run loop polls every 5 ms and shuts down after the count is reached, so an unpaced runtime always
            // executes a few ticks more; it used to diverge two runs from the first tick, and paced was the workaround. Those extra ticks now simulate
            // nothing (SimBridge.HaltAtTick), so the world is exactly the requested twenty ticks', and unpaced it is three runs in a fraction of a second
            // instead of two seconds each.
            config.Unpaced = true;
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
    [Category("Integration")]

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

    /// <summary>
    /// The same, for the pooled players — the fingerprint B2/B3's refactor has to preserve.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the creature fingerprint above cannot see <c>PlayerThink</c> at all.</b> Extracting the player brain into its own assembly is a
    /// refactor whose entire contract is that behaviour does not change, and until this test existed there was nothing in the repository that would have
    /// noticed if it did: a goal picked differently, an activity timer off by one, a destination drawn from a different point in the random sequence would all
    /// have passed every check and shown up later as a load run that did not match the one before it.
    /// <para>
    /// Long enough to exercise the goal FSM rather than one step of it: the activity split only reveals itself once timers have fired and players have chosen
    /// again, which a twenty-tick run does not reach.
    /// </para>
    /// </remarks>
    [Test]
    [Category("Integration")]
    // Was quarantined against #1395, for depending on wall-clock pacing: paced at 50 Hz on a loaded box, a runtime does not do the same work in the same
    // window, and two runs of seed 12345 retired different players (fingerprints of 708 against 703). It runs unpaced now, with the exact stop it was
    // waiting for (SimBridge.HaltAtTick) and no overload response to shed systems — the same work every run, however loaded the box.
    public void SamePlayerSeed_ReproducesTheRun_AndADifferentSeedDoesNot()
    {
        var a = RunPlayerFingerprint(12345);
        var b = RunPlayerFingerprint(12345);
        var c = RunPlayerFingerprint(999);

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.Not.Empty, "the fingerprint covers no players, so it would hold for any brain at all");
            Assert.That(a, Does.Contain("/"), "the activity is part of it, not only the position");
            Assert.That(b, Is.EqualTo(a), "the same seed must reproduce the same player run");
            Assert.That(c, Is.Not.EqualTo(a), "a different seed must reach the players' own per-tick draws");
        });
    }

    /// <summary>Runs a world and fingerprints its players.</summary>
    /// <param name="seed">The run seed.</param>
    /// <returns>The fingerprint.</returns>
    private static string RunPlayerFingerprint(int seed)
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.WarmTicks = 0;

            // Two hundred ticks, not twenty: an activity runs for a timer's worth of ticks before the brain chooses again, so a short run fingerprints one
            // decision per player and would miss a refactor that changed the SECOND one.
            config.MeasuredTicks = 200;

            // 50 Hz: every timer in the brain counts TICKS, so the rate only decides how the seconds-denominated intervals convert. Kept as it was so the
            // fingerprint describes the world it always did.
            config.TickRateHz = 50;
            config.WorkerCount = 1;
            config.Seed = seed;

            // Unpaced, for the reason RunFingerprint gives.
            config.Unpaced = true;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            sim.Run();
            return S0.PlayerFingerprint(sim);
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
