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
/// P1-13b — S2b: a session's share of the push events becomes a <c>TICK</c> message.
/// </summary>
/// <remarks>
/// <para>
/// Every case drives the real track through <see cref="FrameHarness"/>: the blocks step over a real engine's clusters, the projection pass, the push index,
/// then the assembler — and reads the result back through the send side's own hand-off protocol. A fixture that fed the assembler synthetic records would prove
/// nothing about the bytes, because the bytes come from the replication blocks S1 wrote.
/// </para>
/// <para>
/// <b>A skipped session is skipped by the engine, not by a switch.</b> Not draining a session's frames leaves <c>K</c> outstanding, and the producer's own
/// <see cref="SessionSendState.TryBeginFrame"/> then refuses it — which is the skip SUB-03 is about, reached the way production reaches it.
/// </para>
/// <para>
/// <b>Sessions open after tick 1</b>, so their first frame is the fill, and ticks run back to back: a gap in the tick numbers is a missed tick to the push
/// log, and a session that seems to have missed one is caught up or reset.
/// </para>
/// <para>
/// <b>Entities are mutated through <c>ClusterRef.GetSpan</c></b>, the write path that signals nothing, and pushed with <c>Replicate</c> as an explicit
/// profile's system must — or, where the span covers the spatial column, pushed by the engine for the whole cluster.
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

    private static SubscriptionsOptions Options(int enterBudget = 500) =>
        new()
        {
            MaxSessions = 64,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
            EnterBudgetPerFrame = enterBudget,
        };

    private FrameHarness Create(SubscriptionsOptions options = null)
    {
        var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, "FrameAssemblerTests", options ?? Options());

        // The fence publishes the structure marks the engine's own pushes ride: a spawn, a destroy, a spatial write.
        harness.RunFence = true;
        return harness;
    }

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
        harness.RunTick(1);
        var session = harness.OpenSessions(1, Profile)[0];

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
        harness.RunTick(1);
        var session = harness.OpenSessions(1, Profile)[0];

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

    // ── Flags ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>VIEW_COMPLETE</c> is set on the frame that completes the initial fill, and on none before it.</summary>
    [Test]
    public void ViewCompleteIsSetWhenTheFillUnderTheEnterBudgetCompletes()
    {
        using var harness = Create(Options(enterBudget: 4));

        // Spread over several cells, so the budget has cells to spread over frames.
        var positions = new double[10];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = 10.0 + (i * 600.0);
        }

        SpawnCreaturesAt(harness, positions);
        harness.RunTick(1);
        var session = harness.OpenSessions(1, Profile)[0];

        var entered = new List<uint>();
        var completeAt = -1;
        var incompleteWhileMissing = true;
        for (var tick = 2; tick <= 12 && completeAt < 0; tick++)
        {
            harness.RunTick(tick);
            var frame = harness.Read(session);
            if (frame == null)
            {
                continue;
            }

            entered.AddRange(frame.Enters);
            if ((frame.Flags & TickFlags.ViewComplete) != 0)
            {
                completeAt = tick;
            }
            else
            {
                incompleteWhileMissing &= entered.Count < positions.Length;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(completeAt, Is.GreaterThan(2), "the budget binds, so the fill takes more than one frame");
            Assert.That(incompleteWhileMissing, Is.True, "no frame claimed an incomplete view before the last entity entered");
            Assert.That(entered.Count, Is.EqualTo(positions.Length), "nothing was lost to the budget");
            Assert.That(entered, Is.Unique);
        });
    }

    /// <summary>A profile switch tells the client to clear its store, and the view refills from nothing.</summary>
    [Test]
    public void AProfileSwitchResetsTheView()
    {
        using var harness = Create();
        SpawnCreatures(harness, 5);
        harness.RunTick(1);
        var session = harness.OpenSessions(1, Profile)[0];

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

    // ── SUB-03 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A session skipped for K ticks across a mix of enters, changes and leaves converges completely on its next frame: its replica equals the server's
    /// projection, entity for entity and value for value.
    /// </summary>
    /// <remarks>
    /// The reference is a second session on the same profile drained every tick, so "the server's projection" is not a number this fixture computes — it is
    /// what a session that missed nothing holds. That is what SUB-03 is about: a session's state moves only with a published frame, and a skipped one is
    /// caught up from the push log, so two sessions that were sent different subsets of the frames must agree once both are current.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-03")]
    public void ASkippedSessionConvergesOnItsNextFrame()
    {
        using var harness = Create();
        AssertConverges(harness);
    }

    /// <summary>
    /// The same scenario against a push path that commits a skipped session as though its frame had been published: the verifier above must reject it.
    /// </summary>
    /// <remarks>
    /// The mutant is the one move SUB-03 forbids, applied to the production path rather than to a copy of it: the session's last tick moves past ticks it
    /// was never sent, so its next frame catches up from the wrong point and the changes in between never reach it.
    /// </remarks>
    [Test]
    [RuleMutant("SUB-03")]
    public void ASessionCommittedOnASkippedTickIsDetected()
    {
        using var harness = Create();
        harness.Subscriptions.Push.CommitOnSkipForTest = true;
        RuleMutants.AssertDetects("SUB-03", ConvergenceMarker, () => AssertConverges(harness));
    }

    private static void AssertConverges(FrameHarness harness)
    {
        var creatures = SpawnCreatures(harness, 8).ToList();
        harness.RunTick(1);
        var sessions = harness.OpenSessions(2, Profile);
        var current = sessions[0];
        var skipped = sessions[1];

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
        var catchUps = harness.Subscriptions.Push.LogCatchUps;

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

        Assert.That(harness.Subscriptions.Push.LogCatchUps, Is.GreaterThan(catchUps), "the skipped session was caught up from the log, not reset");
    }

    // ── What is projected ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// SUB-10 — what is projected is exactly the push set: two slots written and pushed are the two slots the projection addresses, and nothing else is.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-10")]
    [VerifiesRule("SUB-13")]
    public void TheProjectionAddressesExactlyThePushedSlots()
    {
        using var harness = Create();
        SpawnCreatures(harness, 12);
        harness.RunTick(1);
        var session = harness.OpenSessions(1, Profile)[0];
        harness.RunTick(2);
        harness.Deliver(session);

        var state = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjCreature))];
        state.ResetProjectionCounters();
        SetLevelOf(harness, [2, 5], 777);
        harness.RunTick(3);
        var frame = harness.Read(session);

        Assert.Multiple(() =>
        {
            Assert.That(state.SlotsProjected, Is.EqualTo(2), "the two pushed slots, and not the other ten of the cluster");
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.States.Count, Is.EqualTo(2), "and the two changes reached the client");
        });
    }

    /// <summary>SUB-13 — an archetype no profile observes gets no block, no identity and no projection, however many entities it holds.</summary>
    [Test]
    [VerifiesRule("SUB-13")]
    public void AnArchetypeNoProfileObservesCostsNothing()
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            ProjectionTestSchema.DeclareRock(subs);
            subs.Profile(Profile, p => p.World().Of<ProjCreature>());
        }, "FrameAssemblerTests", Options());
        harness.RunFence = true;
        SpawnCreatures(harness, 4);
        SpawnRocks(harness, 20);
        var session = harness.OpenSessions(1, Profile)[0];
        for (var tick = 1; tick <= 3; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var rocks = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjRock))];
        var creatures = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjCreature))];
        Assert.Multiple(() =>
        {
            Assert.That(creatures.Directory.Count, Is.GreaterThan(0), "the observed archetype has its blocks, or the contrast proves nothing");
            Assert.That(rocks.Directory.Count, Is.Zero, "the unobserved archetype holds no block");
            Assert.That(rocks.SlotsProjected, Is.Zero, "and is never projected");
        });
    }

    /// <summary>Two sessions in the same state receive the same bytes, whether the frame stage runs on one worker or on several.</summary>
    [Test]
    public void TwoSessionsInTheSameStateReceiveTheSameBytes([Values(1, 4)] int workers)
    {
        using var harness = Create();
        SpawnCreatures(harness, 16);
        SpawnRocks(harness, 3);
        harness.RunTick(1);
        var sessions = harness.OpenSessions(2, Profile);

        for (var tick = 2; tick <= 5; tick++)
        {
            if (tick == 4)
            {
                SetLevelOf(harness, [1, 3, 7], (ushort)(900 + tick));
            }

            harness.RunTick(tick, workers);
            var first = harness.Collect(sessions[0]);
            var second = harness.Collect(sessions[1]);
            Assert.That(first, Has.Count.EqualTo(second.Count), $"tick {tick}: both sessions produced the same number of frames");
            for (var i = 0; i < first.Count; i++)
            {
                Assert.That(Convert.ToHexString(second[i]), Is.EqualTo(Convert.ToHexString(first[i])), $"tick {tick}, frame {i}");
            }
        }
    }

    // ── Cost ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A frame of ten thousand records is assembled, encoded and published without a single managed allocation.</summary>
    /// <remarks>
    /// Measured across the frame stage alone — the projection, the push index and the mutation that produced the records are outside the window — because
    /// SUB-07 is about the per-session path that runs once per session per tick. The two warm-up frames grow every native buffer and settle the pool's size
    /// class, which is the rule's "structural growth is exempt" note made concrete.
    /// </remarks>
    [Test]
    public void ATenThousandRecordFrameEncodesWithZeroManagedAllocation()
    {
        const int Entities = 10_000;

        using var harness = Create(Options(enterBudget: Entities * 2));
        SpawnCreatures(harness, Entities);
        harness.RunTick(1);
        var session = harness.OpenSessions(1, Profile)[0];

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

    /// <summary>Changes the vitals group of every live creature in every cluster, and pushes each slot it wrote.</summary>
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
                harness.Subscriptions.Commands.Replicate(in cluster, slot);
            }
        }

        accessor.Dispose();
        tx.Commit();
    }

    /// <summary>Writes the level of the given slots of the first cluster through an Ai span only, and pushes each, as an explicit system must.</summary>
    private static void SetLevelOf(FrameHarness harness, int[] slots, ushort level)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
#pragma warning disable TYPHON009
            var ai = cluster.GetSpan(ProjCreature.Ai);
#pragma warning restore TYPHON009
            foreach (var slot in slots)
            {
                ai[slot].Level = level;
                harness.Subscriptions.Commands.Replicate(in cluster, slot);
            }

            cluster.MarkDirty(ProjCreature.Ai);
            break;
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
