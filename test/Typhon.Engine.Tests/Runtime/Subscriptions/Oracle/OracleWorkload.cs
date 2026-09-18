using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The seeded churn the differential oracle replicates: spawns, destroys, drifts, teleports and writes to both change groups, mixed by a seeded generator
/// so a failure is a seed and a tick rather than a story.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generator and not a script.</b> A hand-written scenario asserts what its author already suspected. The defects this oracle exists to catch —
/// a baseline advanced on a tick that produced nothing, a netId reused inside a skip window, a leave that never reached a session that was behind — are
/// products of ORDER, and order is the one thing a hand-written scenario fixes. A seeded generator covers orders nobody thought to write down, and replays
/// any of them exactly from its seed.
/// </para>
/// <para>
/// <b>It keeps no shadow copy of the world.</b> Every expectation is read back from the ECS at comparison time by <see cref="OracleHarness"/>, because a shadow
/// model is a second implementation that can drift from the engine in exactly the ways a destroy-and-respawn makes likely — and then the oracle reports the
/// model's bug as the engine's. What the workload records is only what the ECS cannot answer afterwards: how many identities it has retired.
/// </para>
/// <para>
/// <b>Writes go through <c>GetSpan</c>.</b> That is the write path that marks no slot dirty and raises no modified flag (SUB-10), so a projection that
/// quietly depended on a dirty bit would replicate nothing here. It is also what <c>FrameAssemblerTests</c> and <c>ProjectionPassTests</c> use, for the same
/// reason.
/// </para>
/// <para>
/// <b>No replicated field is written with the value a client that received nothing would hold.</b> <c>ArchetypeStore</c> zeroes a slot's columns when it
/// allocates one, so zero is the decoder's "never told" value, and a field whose expectation happens to be zero passes whether it arrived or not.
/// <c>mode</c> is generated in 1..4 and <c>alerted</c> is always 1: the point is that the fields CHANGE and that the change is carried, not that they span
/// their domain. <c>hp</c> is the exception — a zero health fraction is a real state worth replicating.
/// </para>
/// <para>
/// <b>It writes <c>template</c> exactly once, at spawn.</b> That is what makes the one <c>OnEnter</c> field predictable: the entity's current value is by
/// construction what its last ENTER carried, so the oracle compares it exactly. It is also the most valuable field it compares — a template from a
/// monotonic counter is near-unique per spawn, so a netId re-leased to a different entity is caught even when position and vitals coincide.
/// </para>
/// </remarks>
internal sealed class OracleWorkload
{
    /// <summary>How far a creature drifts in one small step: below the declared 5 cm tolerance, so the projection is entitled to send nothing.</summary>
    private const float SmallMoveM = 0.02f;

    /// <summary>How far a teleport travels: past the declared 20 m/s at a 10 Hz tick, so the projection must change motion epoch.</summary>
    private const float TeleportSpanM = 600f;

    /// <summary>The margin kept from the grid's edge, so a teleport lands on a coordinate the seed chose rather than one the clamp did.</summary>
    private const float EdgeMarginM = 64f;

    private readonly FrameHarness _harness;
    private readonly Random _random;
    private readonly List<EntityId> _creatures = [];
    private readonly List<EntityId> _rocks = [];

    private int _nextTemplate = 1;

    /// <summary>Builds a workload over a harness's engine.</summary>
    /// <param name="harness">The harness whose engine is churned.</param>
    /// <param name="seed">The generator's seed; the whole run replays from it.</param>
    public OracleWorkload(FrameHarness harness, int seed)
    {
        ArgumentNullException.ThrowIfNull(harness);

        _harness = harness;
        _random = new Random(seed);
    }

    /// <summary>The moving entities alive right now.</summary>
    public IReadOnlyList<EntityId> Creatures => _creatures;

    /// <summary>The static entities alive right now.</summary>
    public IReadOnlyList<EntityId> Rocks => _rocks;

    /// <summary>How many entities have been destroyed, which is what makes netId reuse reachable at all.</summary>
    public int Destroyed { get; private set; }

    /// <summary>How many entities have been spawned.</summary>
    public int Spawned { get; private set; }

    /// <summary>How many teleports were applied, each of which forces a motion epoch change.</summary>
    public int Teleports { get; private set; }

    /// <summary>Seeds the world with a starting population, before any session is opened.</summary>
    /// <param name="creatures">How many moving entities.</param>
    /// <param name="rocks">How many static ones.</param>
    public void Seed(int creatures, int rocks)
    {
        SpawnCreatures(creatures);
        SpawnRocks(rocks);
    }

    /// <summary>
    /// Applies one tick's worth of churn.
    /// </summary>
    /// <remarks>
    /// The mix leans towards movement and state writes because that is what a running world mostly does; spawns and destroys are rarer and are what put
    /// pressure on identity. Each branch commits its own transaction, which is also what a real schedule produces — several systems committing in one tick.
    /// </remarks>
    public void Step()
    {
        var roll = _random.Next(100);
        if (roll < 12)
        {
            SpawnCreatures(1 + _random.Next(2));
        }
        else if (roll < 24)
        {
            DestroyFrom(_creatures, 1);
        }
        else if (roll < 30)
        {
            SpawnRocks(1);
        }
        else if (roll < 34)
        {
            DestroyFrom(_rocks, 1);
        }
        else if (roll < 60)
        {
            MoveCreatures(small: true);
        }
        else if (roll < 74)
        {
            MoveCreatures(small: false);
        }
        else if (roll < 88)
        {
            WriteVitals();
        }
        else
        {
            WriteMode();
        }
    }

    private void SpawnCreatures(int count)
    {
        using var tx = _harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = BoundsAt(RandomCoordinate(), RandomCoordinate());
            var ai = new ProjAi
            {
                Template = (byte)(_nextTemplate++ & 0xFF),
                Mode = (ProjAiMode)_random.Next(5),
                Alerted = (byte)_random.Next(2),
                Level = (ushort)_random.Next(1, 500),
                ThinkCooldown = _random.Next(1000),
            };

            var vitals = new ProjVitals { Health = _random.Next(1, 21), MaxHealth = 20 };
            _creatures.Add(tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals)));
            Spawned++;
        }

        tx.Commit();
    }

    private void SpawnRocks(int count)
    {
        using var tx = _harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = BoundsAt(RandomCoordinate(), RandomCoordinate());
            var ai = new ProjAi { Template = (byte)(_nextTemplate++ & 0xFF) };
            _rocks.Add(tx.Spawn<ProjRock>(ProjRock.Bounds.Set(in bounds), ProjRock.Ai.Set(in ai)));
            Spawned++;
        }

        tx.Commit();
    }

    private void DestroyFrom(List<EntityId> from, int count)
    {
        if (from.Count == 0)
        {
            return;
        }

        using var tx = _harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count && from.Count > 0; i++)
        {
            var index = _random.Next(from.Count);
            tx.Destroy(from[index]);
            from.RemoveAt(index);
            Destroyed++;
        }

        tx.Commit();
    }

    /// <summary>
    /// Moves a handful of creatures, either by a hair or across the world.
    /// </summary>
    /// <param name="small">
    /// <see langword="true"/> drifts by less than the declared tolerance, which is the case where the projection is entitled to send nothing and a naive
    /// oracle would insist on a segment; <see langword="false"/> exceeds the declared teleport speed, which forces a motion epoch change and an absolute
    /// re-anchor — the move that has to survive a session being behind.
    /// </param>
    private void MoveCreatures(bool small)
    {
        if (small)
        {
            // A drift, through the write path that signals nothing (SUB-10). It stays inside the cluster's bounds, so nothing migrates and the projection
            // is entitled to send no segment at all.
            ForEachChosenCreature((ref ProjBounds bounds, ref ProjAi ai, ref ProjVitals vitals) =>
            {
                var x = Clamp(Centre(bounds.Bounds.MinX, bounds.Bounds.MaxX) + (((float)_random.NextDouble() - 0.5f) * 2f * SmallMoveM));
                var y = Clamp(Centre(bounds.Bounds.MinY, bounds.Bounds.MaxY) + (((float)_random.NextDouble() - 0.5f) * 2f * SmallMoveM));
                bounds = BoundsAt(x, y);
            });

            return;
        }

        Teleports += Teleport();
    }

    /// <summary>
    /// Teleports a few creatures across the world through <c>WriteSpatial</c>, so they really change cluster.
    /// </summary>
    /// <returns>How many entities were moved.</returns>
    /// <remarks>
    /// <b><c>WriteSpatial</c>, not <c>GetSpan</c>, and that is the whole point of this branch.</b> A spatial field written through <c>GetSpan</c> is not seen
    /// by the spatial index, so the entity keeps its cluster however far its coordinates move — the position changes but nothing MIGRATES. Migration is what
    /// makes the engine re-identify an entity in a new cluster, release the old slot's identity and lease a new one, which is the path SUB-09 is about and
    /// the one a teleport is supposed to exercise. Writing it the other way produced a large coordinate jump and no migration at all.
    /// </remarks>
    private int Teleport()
    {
        if (_creatures.Count == 0)
        {
            return 0;
        }

        var wanted = 1 + _random.Next(3);
        var targets = new HashSet<int>();
        for (var i = 0; i < wanted; i++)
        {
            targets.Add(_random.Next(_creatures.Count));
        }

        var moved = 0;
        var seen = 0;
        using var tx = _harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (!targets.Contains(seen++))
                    {
                        continue;
                    }

                    cluster.WriteSpatial(ProjCreature.Bounds, slot, BoundsAt(Clamp(RandomCoordinate()), Clamp(RandomCoordinate())));
                    moved++;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
        return moved;
    }

    /// <summary>Writes the <c>vitals</c> group — <c>level</c> and the health fraction — on a few creatures.</summary>
    private void WriteVitals() => ForEachChosenCreature((ref ProjBounds bounds, ref ProjAi ai, ref ProjVitals vitals) =>
    {
        ai.Level = (ushort)_random.Next(1, 500);
        vitals.Health = _random.Next(0, vitals.MaxHealth + 1);
    });

    /// <summary>Writes the ungrouped fields — <c>mode</c> and <c>alerted</c> — plus the field nobody replicates, which must reach no client.</summary>
    private void WriteMode() => ForEachChosenCreature((ref ProjBounds bounds, ref ProjAi ai, ref ProjVitals vitals) =>
    {
        ai.Mode = (ProjAiMode)_random.Next(5);
        ai.Alerted = (byte)_random.Next(2);
        ai.ThinkCooldown = _random.Next(1000);
    });

    /// <summary>
    /// Applies a writer to one to three randomly chosen live creatures, addressed by cluster and slot.
    /// </summary>
    /// <param name="write">What to write.</param>
    /// <returns>How many slots were written.</returns>
    /// <remarks>
    /// Chosen by position in the cluster walk rather than by <see cref="EntityId"/>, because the point of writing through <c>GetSpan</c> is to write the
    /// cluster the way a system does. The choice is still seeded, so the run replays.
    /// </remarks>
    private int ForEachChosenCreature(SlotWriter write)
    {
        if (_creatures.Count == 0)
        {
            return 0;
        }

        var wanted = 1 + _random.Next(3);
        var targets = new HashSet<int>();
        for (var i = 0; i < wanted; i++)
        {
            targets.Add(_random.Next(_creatures.Count));
        }

        var written = 0;
        var seen = 0;
        using var tx = _harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
            var bounds = cluster.GetSpan(ProjCreature.Bounds);
            var ai = cluster.GetSpan(ProjCreature.Ai);
            var vitals = cluster.GetSpan(ProjCreature.Vitals);
#pragma warning restore TYPHON009
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                if (targets.Contains(seen++))
                {
                    write(ref bounds[slot], ref ai[slot], ref vitals[slot]);
                    written++;
                }
            }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
        return written;
    }

    private float RandomCoordinate() => (float)(((_random.NextDouble() * 2.0) - 1.0) * (ProjectionTestSchema.WorldExtentM - EdgeMarginM));

    private static float Centre(float min, float max) => (min + max) * 0.5f;

    private static float Clamp(float value)
    {
        const float Limit = ProjectionTestSchema.WorldExtentM - EdgeMarginM;
        return value < -Limit ? -Limit : value > Limit ? Limit : value;
    }

    private static ProjBounds BoundsAt(float x, float y) => new()
    {
        Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f },
        Speed = 1f,
    };

    private delegate void SlotWriter(ref ProjBounds bounds, ref ProjAi ai, ref ProjVitals vitals);
}
