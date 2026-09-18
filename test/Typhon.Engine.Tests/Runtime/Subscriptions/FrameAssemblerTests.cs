using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-13b — S2b: a session's hits become a <c>TICK</c> message.
/// </summary>
/// <remarks>
/// <para>
/// Every case drives the real track through <see cref="FrameHarness"/>: interest over a real engine's clusters, the blocks step, the projection pass, then
/// the assembler — and reads the result back through the send side's own hand-off protocol. A fixture that fed the assembler synthetic records would prove
/// nothing about the bytes, because the bytes come from the replication blocks S1 wrote.
/// </para>
/// <para>
/// <b>A skipped session is skipped by the engine, not by a switch.</b> Not draining a session's frames leaves <c>K</c> outstanding, and the producer's own
/// <see cref="SessionSendState.TryBeginFrame"/> then refuses it — which is the skip SUB-03 is about, reached the way production reaches it.
/// </para>
/// <para>
/// <b>Entities are mutated through <c>ClusterRef.GetSpan</c></b>, the write path that signals nothing (SUB-10). It is what a system that does not move an
/// entity between clusters uses, and it keeps a case's cluster layout still while its values change.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class FrameAssemblerTests : TestBase<FrameAssemblerTests>
{
    private const string Profile = "god-world";
    private const string OtherProfile = "god-world-again";

    /// <summary>The marker the convergence assertion emits, which is what proves the mutant was rejected by the verifier and not by its own scaffolding.</summary>
    private const string ConvergenceMarker = "the skipped session did not converge";

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>().Of<ProjRock>());
        subs.Profile(OtherProfile, p => p.World().Of<ProjCreature>().Of<ProjRock>());
    }

    private static SubscriptionsOptions Options(int enterBudget = 500, bool changedOnlyGather = true) =>
        new()
        {
            MaxSessions = 64,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
            EnterBudgetPerFrame = enterBudget,
            ChangedOnlyGather = changedOnlyGather,
        };

    private FrameHarness Create(SubscriptionsOptions options = null) =>
        FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, "FrameAssemblerTests", options ?? Options());

    // ── The grammar ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The four sub-lists travel in the grammar's order, each ascending by netId — which is what the gap encoding is relative to — and one block per
    /// archetype.
    /// </summary>
    [Test]
    public void TheFourSubListsTravelInOrderEachAscendingByNetId()
    {
        using var harness = Create();
        SpawnCreatures(harness, 12);
        SpawnRocks(harness, 5);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.PrimeBlocks();
        harness.RunTick(2);
        var fill = harness.Read(session);

        MoveFirst(harness, 4);
        DamageFirst(harness, 4, 7);
        harness.RunTick(3);
        var update = harness.Read(session);

        Assert.Multiple(() =>
        {
            Assert.That(fill, Is.Not.Null, "the first frame is the initial fill");
            Assert.That(fill.Enters, Is.Ordered.Ascending, "an ENTITIES sub-list is gap-encoded, so it has to be ascending by netId");
            Assert.That(fill.Enters, Is.Unique);
            Assert.That(fill.Enters.Count, Is.EqualTo(17), "every live entity of both archetypes entered");
            Assert.That(fill.Blocks, Is.Unique, "at most one ENTITIES block per archetype in a frame (03 § 10)");
            Assert.That(fill.Blocks.Count, Is.EqualTo(2), "one for the creatures, one for the rocks");

            Assert.That(update, Is.Not.Null);
            Assert.That(update.Segments.Count, Is.EqualTo(4), "four creatures moved far enough for a segment");
            Assert.That(update.States.Count, Is.EqualTo(3), "three creatures changed a group");
            Assert.That(update.Segments, Is.Ordered.Ascending);
            Assert.That(update.States.Select(s => s.NetId).ToArray(), Is.Ordered.Ascending);
            Assert.That(update.Blocks, Is.EqualTo(new[] { nameof(ProjCreature) }), "the rocks changed nothing, so they get no block at all");
            Assert.That(KindOrder(update), Is.Ordered.Ascending, "the sub-lists are written in the grammar's order");
        });
    }

    /// <summary>Leaves are the last records of their block, after the enters of the same frame, and never name a netId the same frame enters.</summary>
    [Test]
    public void LeavesAreLastAndNoNetIdBothEntersAndLeaves()
    {
        using var harness = Create();
        var first = SpawnCreatures(harness, 6);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.PrimeBlocks();
        harness.RunTick(2);
        Assert.That(harness.Read(session).Enters.Count, Is.EqualTo(6));

        // Three go and three arrive: one frame carrying both enters and leaves, for disjoint identities.
        Destroy(harness, first.Take(3));
        SpawnCreatures(harness, 3);
        harness.RunTick(3);
        var frame = harness.Read(session);

        Assert.Multiple(() =>
        {
            Assert.That(frame.Leaves.Count, Is.EqualTo(3), "the three destroyed creatures left");
            Assert.That(frame.Enters.Count, Is.EqualTo(3), "the three new creatures entered");
            Assert.That(frame.Enters.Intersect(frame.Leaves), Is.Empty, "a frame never carries an enter and a leave for one netId (03 § 10)");
            Assert.That(frame.Leaves, Is.Ordered.Ascending);

            var lastEnter = frame.Calls.FindLastIndex(c => c.StartsWith("enter", StringComparison.Ordinal));
            var firstLeave = frame.Calls.FindIndex(c => c.StartsWith("leave", StringComparison.Ordinal));
            Assert.That(firstLeave, Is.GreaterThan(lastEnter), "leaves come last in the block");
        });
    }

    /// <summary>
    /// A known entry whose generation no longer matches the hit leaves in this frame, and the hit enters in the next one (02 § 5).
    /// </summary>
    /// <remarks>
    /// The stale state is written into the session's known-set directly. A correct server cannot reach it — the netId quarantine is longer than a session may
    /// go without a frame — which is exactly why 02 § 5 calls this the defensive path, and why a test that waited for the engine to produce one would be
    /// waiting for a bug.
    /// <para>
    /// <b>Pinned to the full walk</b> (<c>ChangedOnlyGather = false</c>). The changed-only gather visits only the slots S1 reported as changed, and this
    /// corruption is by construction something nothing reported: the entity is untouched, so its slot carries no bit and no probe reaches it. That is sound
    /// where the state is reachable — a real reused identity bumps the hot entry's generation, which IS a change, so the slot is visited on the tick it
    /// happens, and a session that missed that tick is by definition behind and takes the full walk anyway. What the fast path cannot do is discover a
    /// corruption that nothing reported, which is what the defensive branch is for and what this fixture exercises.
    /// </para>
    /// </remarks>
    [Test]
    public void AStaleGenerationLeavesInThisFrameAndEntersInTheNext()
    {
        using var harness = Create(Options(changedOnlyGather: false));
        SpawnCreatures(harness, 4);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.PrimeBlocks();
        harness.RunTick(2);
        var target = harness.Read(session).Enters[1];

        var known = harness.StateOf(session).Known;
        Assert.That(known.Probe(target, 0, out var entry), Is.Not.EqualTo(KnownProbe.Unknown), "the fill made the entity known");
        var real = entry->Generation;
        known.Remove(target);
        Assert.That(known.AddSource(target, (ushort)(real + 1), 1, 0, out _), Is.EqualTo(KnownAdd.Entered), "re-learned under a generation nobody carries");

        harness.RunTick(3);
        var leaving = harness.Read(session);

        harness.RunTick(4);
        var entering = harness.Read(session);

        Assert.Multiple(() =>
        {
            Assert.That(leaving, Is.Not.Null);
            Assert.That(leaving.Leaves, Does.Contain(target), "a stale generation is a leave now");
            Assert.That(leaving.Enters, Does.Not.Contain(target), "and never an enter in the same frame");
            Assert.That(entering, Is.Not.Null);
            Assert.That(entering.Enters, Does.Contain(target), "the hit enters in the following frame");
            Assert.That(entering.Leaves, Is.Empty);
        });
    }

    // ── Flags ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>VIEW_COMPLETE</c> is set on the frame that completes the initial fill, and on none before it.</summary>
    [Test]
    public void ViewCompleteIsSetWhenTheFillUnderTheEnterBudgetCompletes()
    {
        using var harness = Create(Options(enterBudget: 4));
        SpawnCreatures(harness, 10);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.PrimeBlocks();
        var flags = new List<TickFlags>();
        var entered = new List<uint>();
        for (var tick = 2; tick <= 4; tick++)
        {
            harness.RunTick(tick);
            var frame = harness.Read(session);
            flags.Add(frame.Flags);
            entered.AddRange(frame.Enters);
        }

        Assert.Multiple(() =>
        {
            Assert.That(flags[0] & TickFlags.ViewComplete, Is.EqualTo(TickFlags.None), "four of ten entered: the view is not filled");
            Assert.That(flags[1] & TickFlags.ViewComplete, Is.EqualTo(TickFlags.None), "eight of ten");
            Assert.That(flags[2] & TickFlags.ViewComplete, Is.EqualTo(TickFlags.ViewComplete), "the last two complete the fill");
            Assert.That(entered.Count, Is.EqualTo(10), "nothing was lost to the budget");
            Assert.That(entered, Is.Unique);
            Assert.That(harness.Assembler.EntersDeferred, Is.EqualTo(8), "six deferred on the first frame, two on the second");
        });
    }

    /// <summary>A profile switch tells the client to clear its store, and the view refills from nothing.</summary>
    [Test]
    public void AProfileSwitchResetsTheView()
    {
        using var harness = Create();
        SpawnCreatures(harness, 5);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.PrimeBlocks();
        harness.RunTick(2);
        var first = harness.Read(session);

        harness.RunTick(3);
        Assert.That(harness.HasFrame(session), Is.False, "nothing changed, so no frame is produced at all");

        Assert.That(harness.Sessions.SetProfile(session, OtherProfile), Is.True);
        harness.RunTick(4);
        var reset = harness.Read(session);

        Assert.Multiple(() =>
        {
            Assert.That(first.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.None), "a brand-new session's store is already empty");
            Assert.That(first.Flags & TickFlags.ViewComplete, Is.EqualTo(TickFlags.ViewComplete), "and its fill completed inside the budget");
            Assert.That(reset, Is.Not.Null);
            Assert.That(reset.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset), "a profile switch is a RESET (03 § 5)");
            Assert.That(reset.Enters.Count, Is.EqualTo(5), "the view refills from nothing");
            Assert.That(reset.Flags & TickFlags.ViewComplete, Is.EqualTo(TickFlags.ViewComplete), "and the refill completes within the budget");
        });
    }

    // ── The enter budget ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the budget binds, the candidates it lets through are the ones nearest the session's focus, and the rest are deferred rather than dropped.
    /// </summary>
    /// <remarks>
    /// The expectation is computed from the CLIENT's own replica — the positions it decoded on the first frame — so "nearest" is measured in the same
    /// coordinates the wire carries, and the fixture never has to agree with the engine about a quantization.
    /// </remarks>
    [Test]
    public void TheEnterBudgetDefersNearestFirst()
    {
        const int Budget = 3;
        const int Count = 10;

        using var harness = Create(Options(enterBudget: Budget));

        // Ten creatures on a line 4 m apart: far coarser than the 2^-10 m position quantum, so the ranking is unambiguous.
        var positions = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            positions[i] = 100.0 + (i * 4.0);
        }

        SpawnCreaturesAt(harness, positions);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.PrimeBlocks();

        // The budget already binds on the initial fill, so it takes four frames to give every creature an identity — which is itself the "nothing is lost"
        // half of the rule, and is what gives the replica the positions the expectation below is computed from.
        var replica = harness.Replica(session);
        var creatureIdx = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;
        for (var tick = 2; tick <= 6; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var all = replica.NetIds(creatureIdx);
        Assert.That(all, Has.Length.EqualTo(Count), "the fill completed over several frames, losing nothing to the budget");

        // The focus sits beyond the far end of the line, so the nearest are the entities furthest along it — an order that is neither hit order nor netId
        // order, and therefore the only one the budget could have produced.
        var focusX = positions[^1] + 20.0;
        var plan = harness.Subscriptions.PlanNamed(nameof(ProjCreature));
        var expected = all
            .OrderBy(id => Math.Abs(replica.Position(creatureIdx, id)[0] - focusX))
            .Take(Budget)
            .OrderBy(id => id)
            .ToArray();

        harness.Assembler.SetFocus(session, Code(plan, 0, focusX), Code(plan, 1, 10.0));

        // A fresh view over the same entities, so the budget selects from all ten at once.
        harness.StateOf(session).PendingReset = true;
        harness.RunTick(7);
        var frame = harness.Read(session);

        Assert.Multiple(() =>
        {
            Assert.That(frame.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset));
            Assert.That(frame.Enters.Count, Is.EqualTo(Budget), "the budget is what caps the frame");
            Assert.That(frame.Enters.OrderBy(x => x).ToArray(), Is.EqualTo(expected), "the three nearest the focus entered");
            Assert.That(frame.Flags & TickFlags.ViewComplete, Is.EqualTo(TickFlags.None), "seven are still owed, so the view is not complete");
        });

        // And the deferred ones arrive over the following frames, with nothing lost.
        var seen = new List<uint>(frame.Enters);
        for (var tick = 8; tick <= 12; tick++)
        {
            harness.RunTick(tick);
            var next = harness.Read(session);
            if (next != null)
            {
                seen.AddRange(next.Enters);
            }
        }

        Assert.That(seen.OrderBy(x => x).ToArray(), Is.EqualTo(all), "every deferred entity entered in a later frame");
    }

    // ── SUB-03 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A session skipped for K ticks across a mix of enters, changes and leaves converges completely on its next frame: its replica equals the server's
    /// projection, entity for entity and value for value.
    /// </summary>
    /// <remarks>
    /// The reference is a second session on the same profile drained every tick, so "the server's projection" is not a number this fixture computes — it is
    /// what a session that missed nothing holds. That is what SUB-03 is about: records are absolute, so two sessions that were sent different subsets of the
    /// frames must agree once both are current.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-03")]
    public void ASkippedSessionConvergesOnItsNextFrame()
    {
        using var harness = Create();
        AssertConverges(harness);
    }

    /// <summary>
    /// The same scenario against an assembler that advances a skipped session's baseline: the verifier above must reject it.
    /// </summary>
    /// <remarks>
    /// The mutant is the one move SUB-03 forbids — a baseline advanced by a tick that produced no frame — and it is applied to the production assembler
    /// rather than to a copy of it, so what is proven falsifiable is the real path.
    /// </remarks>
    [Test]
    [RuleMutant("SUB-03")]
    public void ABaselineThatAdvancesOnASkippedTickIsDetected()
    {
        using var harness = Create();
        harness.Assembler.BaselineAdvancesOnSkipForTest = true;
        RuleMutants.AssertDetects("SUB-03", ConvergenceMarker, () => AssertConverges(harness));
    }

    private static void AssertConverges(FrameHarness harness)
    {
        var creatures = SpawnCreatures(harness, 8).ToList();
        var sessions = harness.OpenSessions(2, Profile);
        var current = sessions[0];
        var skipped = sessions[1];

        harness.PrimeBlocks();
        harness.RunTick(2);
        harness.Deliver(current);
        harness.Deliver(skipped);

        // The skip window's mix, in this order on purpose. The destroy comes FIRST because the engine compacts a cluster around the hole, which re-identifies
        // the entity it moved and makes its next record a full enter — an enter carries every group, so a change that happened before it is resent whatever a
        // baseline says. Everything after the destroy is a pure value change, which is the only kind of record a wrong baseline can swallow.
        var window = new Action<FrameHarness>[]
        {
            h => { Destroy(h, creatures.Take(1)); creatures.RemoveAt(0); creatures.AddRange(SpawnCreatures(h, 2)); },
            h => MoveFirst(h, 3, 2000f),
            h => DamageFirst(h, 2, 6, 300),
            h => DamageFirst(h, 0, 4, 400),
            h => MoveFirst(h, 3, 3000f),
            h => DamageFirst(h, 1, 7, 500),
        };

        // From here the skipped session is never drained: two frames fill its slots and every later tick finds none free.
        var tick = 3L;
        foreach (var step in window)
        {
            step(harness);
            harness.RunTick(tick++);
            harness.Deliver(current);
        }

        Assert.That(harness.Assembler.FramesSkipped, Is.GreaterThan(0), "the scenario has to actually skip the session it is about");

        // The skipped session catches up: its slots free, and its next frame must carry everything it missed.
        harness.Deliver(skipped);
        harness.RunTick(tick);
        harness.Deliver(current);
        harness.Deliver(skipped);

        var reference = harness.Replica(current);
        var recovered = harness.Replica(skipped);

        foreach (var name in new[] { nameof(ProjCreature), nameof(ProjRock) })
        {
            var idx = harness.CatalogPlan.ArchetypeByName(name).Idx;
            var expectedIds = reference.NetIds(idx);
            var actualIds = recovered.NetIds(idx);
            if (!expectedIds.AsSpan().SequenceEqual(actualIds))
            {
                Assert.Fail($"{ConvergenceMarker}: for {name} it holds [{string.Join(",", actualIds)}] where the current session holds "
                    + $"[{string.Join(",", expectedIds)}]");
            }

            foreach (var netId in expectedIds)
            {
                foreach (var field in reference.Store.Archetypes[idx].Plan.Fields)
                {
                    if (field.ValueKind != FieldValueKind.Number)
                    {
                        continue;
                    }

                    var expected = reference.Value(idx, netId, field.Name);
                    var actual = recovered.Value(idx, netId, field.Name);
                    if (expected != actual)
                    {
                        Assert.Fail($"{ConvergenceMarker}: {name} {netId}'s '{field.Name}' is {actual} where the current session holds {expected}");
                    }
                }
            }
        }

        if (recovered.Store.Anomalies != 0)
        {
            Assert.Fail($"{ConvergenceMarker}: the recovered replica counted {recovered.Store.Anomalies} anomalies");
        }
    }

    // ── Cost ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A frame of ten thousand records is assembled, encoded and published without a single managed allocation.</summary>
    /// <remarks>
    /// Measured across the frame stage alone — the interest pass, the projection and the mutation that produced the records are outside the window — because
    /// SUB-07 is about the per-session path that runs once per session per tick. The two warm-up frames grow every native buffer and settle the pool's size
    /// class, which is the rule's "structural growth is exempt" note made concrete.
    /// </remarks>
    [Test]
    public void ATenThousandRecordFrameEncodesWithZeroManagedAllocation()
    {
        const int Entities = 10_000;

        using var harness = Create(Options(enterBudget: Entities * 2));
        SpawnCreatures(harness, Entities);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.PrimeBlocks();
        harness.RunTick(2);
        harness.Deliver(session);

        // Three identical state-record frames before the measured one: they grow every native buffer AND settle both of the session's two frame slots on
        // the class a state frame needs, so the measured frame keeps its block rather than renting one (SUB-07's "structural growth is exempt").
        for (var warm = 3; warm <= 5; warm++)
        {
            DamageAll(harness, 200 + warm);
            harness.RunTick(warm);
            harness.Deliver(session);
        }

        var before = harness.Assembler.RecordsEncoded;
        DamageAll(harness, 300);
        harness.RunUpToFrames(6);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        harness.RunFramesOnly(6);
        var delta = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        harness.Assembler.Gate.Publish(6);
        harness.Deliver(session);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.RecordsEncoded - before, Is.EqualTo(Entities), "the measured frame carried one record per entity");
            Assert.That(delta, Is.Zero, "the frame stage must not allocate; it runs once per session per tick (SUB-07)");
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The record kinds a decoded frame reported, mapped onto the grammar's order so a single assertion can require them to be sorted.</summary>
    private static int[] KindOrder(FrameLog log) => log.Calls
        .Select(c => c.Split(' ')[0])
        .Select(kind => kind switch { "enter" => 0, "segment" => 1, "state" => 2, "leave" => 3, _ => -1 })
        .Where(rank => rank >= 0)
        .ToArray();

    /// <summary>The wire code one axis of an archetype's position codec gives a world coordinate — the space the enter budget ranks in.</summary>
    private static uint Code(CompiledProjectionPlan plan, int axis, double value) =>
        WireMath.EncodeQuant(value, plan.Position.Pos.Min[axis], plan.Position.Pos.Max[axis], plan.Position.Pos.Bits);

    private static EntityId[] SpawnCreatures(FrameHarness harness, int count)
    {
        var positions = new double[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = 10.0 + ((i % 200) * 3.0);
        }

        return SpawnCreaturesAt(harness, positions);
    }

    private static EntityId[] SpawnCreaturesAt(FrameHarness harness, double[] positions)
    {
        var entities = new EntityId[positions.Length];
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < positions.Length; i++)
        {
            var bounds = At((float)positions[i], 10f);
            var ai = new ProjAi { Template = 7, Level = 100, Mode = ProjAiMode.Idle };
            var vitals = new ProjVitals { Health = 5, MaxHealth = 10 };
            entities[i] = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
        }

        tx.Commit();
        return entities;
    }

    private static void SpawnRocks(FrameHarness harness, int count)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var bounds = At(-200f + (i * 5f), -50f);
            var ai = new ProjAi { Template = (byte)(i + 1) };
            tx.Spawn<ProjRock>(ProjRock.Bounds.Set(in bounds), ProjRock.Ai.Set(in ai));
        }

        tx.Commit();
    }

    private static void Destroy(FrameHarness harness, IEnumerable<EntityId> entities)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        foreach (var entity in entities)
        {
            tx.Destroy(entity);
        }

        tx.Commit();
    }

    /// <summary>Teleports the first <paramref name="count"/> occupied slots of the first cluster, which is always far enough to force a segment.</summary>
    private static void MoveFirst(FrameHarness harness, int count, float to = 2000f) =>
        WriteFirstCluster(harness, 0, count, (ref ProjBounds bounds, ref ProjAi ai, int slot) => bounds = At(to + (slot * 7f), to));

    /// <summary>Changes the vitals group of the occupied slots in <c>[from, to)</c> of the first cluster to values derived from <paramref name="seed"/>.</summary>
    private static void DamageFirst(FrameHarness harness, int from, int to, int seed = 200) => WriteFirstCluster(harness, from, to - from,
        (ref ProjBounds bounds, ref ProjAi ai, int slot) => ai.Level = (ushort)(seed + slot));

    /// <summary>Changes the vitals group of every live creature in every cluster.</summary>
    private static void DamageAll(FrameHarness harness, int seed)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
            var ai = cluster.GetSpan(ProjCreature.Ai);
#pragma warning restore TYPHON009
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                ai[slot].Level = (ushort)(seed + slot);
            }
        }

        accessor.Dispose();
        tx.Commit();
    }

    private delegate void SlotWriter(ref ProjBounds bounds, ref ProjAi ai, int slot);

    private static void WriteFirstCluster(FrameHarness harness, int from, int count, SlotWriter write)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
            var bounds = cluster.GetSpan(ProjCreature.Bounds);
            var ai = cluster.GetSpan(ProjCreature.Ai);
#pragma warning restore TYPHON009
            var seen = 0;
            var written = 0;
            while (occupancy != 0 && written < count)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                if (seen++ < from)
                {
                    continue;
                }

                write(ref bounds[slot], ref ai[slot], slot);
                written++;
            }

            break;
        }

        accessor.Dispose();
        tx.Commit();
    }

    private static ProjBounds At(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f }, Speed = 1f };
}
