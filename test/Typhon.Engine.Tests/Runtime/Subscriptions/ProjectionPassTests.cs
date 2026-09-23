using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// P1-09 — S1's per-block pass: project, quantize, compare, stamp, encode.
/// </summary>
/// <remarks>
/// <para>
/// Every case here drives <see cref="ProjectionPass.ProjectBlock"/> against a real engine's clusters, because the properties under test are about the bytes a
/// cluster actually holds: the occupancy word, the entity-id column and the component columns. A synthetic block would let an offset be wrong in exactly the
/// way SUB-01 is about and still pass.
/// </para>
/// <para>
/// <b>The pushed set is supplied by the fixture, not by the push path.</b> What S1 consumes is a block, a marked mask and a claim stamp — data, not the push
/// path's code — so the harness below marks slots through the same <see cref="WatchedBlockList"/> the push marks go into. Driving it from here is also what
/// lets a case mark a slot the live occupancy no longer covers, which is the destroyed-entity path. No push path is attached, so no event is recorded: what
/// is asserted is the entry the event would name.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class ProjectionPassTests : TestBase<ProjectionPassTests>
{
    private const long AmpleBudget = 16L * 1024 * 1024;

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public DatabaseEngine Engine;
        public CompiledProjectionPlan Plan;
        public ArchetypeReplicationState State;
        public NetIdAllocator NetIds;
        public ArchetypeClusterState ClusterState;
        private ResourceRegistry _registry;

        public ReplicationBlockLayout Layout => Plan.BlockLayout;

        /// <summary>Attaches a block to every active cluster and marks every currently occupied slot watched for <paramref name="tick"/>.</summary>
        public void MarkAllWatched(uint tick)
        {
            using var guard = EpochGuard.Enter(Engine.EpochManager);
            using var accessor = ClusterState.ClusterSegment.CreateChunkAccessor();
            var ids = ClusterState.ReadActiveClusterList(out var count);

            // The blocks step comes FIRST, exactly as it does in the track: the watched-block list is sized from the directory, so a cluster that gains its
            // block after the sizing would have nowhere to be listed.
            for (var i = 0; i < count; i++)
            {
                var chunkId = ids[i];
                if (chunkId >= 0 && *(ulong*)accessor.GetChunkAddress(chunkId) != 0 && !State.Directory.TryGetBlock(chunkId, out _))
                {
                    State.TryAttachBlock(chunkId, out _);
                }
            }

            State.WatchedBlocks.ClearMasks();
            State.BeginWatchedBlocks(tick);

            for (var i = 0; i < count; i++)
            {
                var chunkId = ids[i];
                if (chunkId < 0)
                {
                    continue;
                }

                var occupancy = *(ulong*)accessor.GetChunkAddress(chunkId);
                if (occupancy == 0 || !State.Directory.TryGetBlock(chunkId, out var block))
                {
                    continue;
                }

                MarkSlots(block, occupancy);
            }

            Assert.That(State.WatchedBlocks.Overflow, Is.Zero, "the list is sized from the directory, so a mark can never find it full");
        }

        /// <summary>Marks an explicit slot set of one block — used where the live occupancy is not what the push path would have marked.</summary>
        public void MarkSlots(ReplicationBlockHeader* block, ulong slots)
        {
            while (slots != 0)
            {
                var slot = BitOperations.TrailingZeroCount(slots);
                slots &= slots - 1;
                State.WatchedBlocks.Mark(block, slot);
            }
        }

        /// <summary>Runs the pass over every marked block, exactly as the Project stage's single chunk would.</summary>
        public void Project(uint tick)
        {
            State.BeginProjectTick(workers: 1);
            var before = State.RecordsProduced;

            using var guard = EpochGuard.Enter(Engine.EpochManager);
            using var accessor = ClusterState.ClusterSegment.CreateChunkAccessor();
            var count = State.WatchedBlocks.Count;
            for (var i = 0; i < count; i++)
            {
                var block = State.WatchedBlocks[i];
                ProjectionPass.ProjectBlock(Plan, 0, State, 0, block, accessor.GetChunkAddress(block->ChunkId), null, tick);
            }

            Changed = State.RecordsProduced - before;
        }

        /// <summary>Entities the last <see cref="Project"/> found entered or changed.</summary>
        public long Changed;

        /// <summary>Whether a slot's entry was (re-)initialized by the last projection — the entity enters.</summary>
        public bool Entered(int slot) => (Hot(FirstBlock, slot)->Flags & ProjectionPass.FlagInitializedThisTick) != 0;

        /// <summary>Mark then project, which is one tick of the track for this archetype.</summary>
        public void Tick(uint tick)
        {
            MarkAllWatched(tick);
            Project(tick);
        }

        public ReplicationHotEntry* Hot(ReplicationBlockHeader* block, int slot) =>
            (ReplicationHotEntry*)((byte*)block + Layout.HotOffset + (slot * Layout.HotStride));

        public ReplicationBlockHeader* FirstBlock => State.WatchedBlocks[0];

        /// <summary>Identities naming a live entity: the allocator's live count less what the leases are holding unspent.</summary>
        public int NamedEntities => NetIds.LiveCount - State.NetIdLeases.LeasedCount;

        public int TickSlotOf(string group)
        {
            foreach (var g in Plan.Groups)
            {
                if (g.Name == group)
                {
                    return g.TickSlot;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(group), group, "no such group in the plan");
        }

        public int BitOf(string group)
        {
            foreach (var g in Plan.Groups)
            {
                if (g.Name == group)
                {
                    return g.Bit;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(group), group, "no such group in the plan");
        }

        public void Dispose()
        {
            State?.Dispose();
            NetIds?.Dispose();
            _registry?.Dispose();
            Engine?.Dispose();
        }

        public static Harness Create(IServiceProvider services, int entities, float spread = 0f)
        {
            var engine = ProjectionTestSchema.SetupEngine(services);
            var plan = ProjectionTestSchema.PlanFor(ProjectionTestSchema.CompileAll(engine), nameof(ProjCreature));
            var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ProjectionPassTests" });
            var netIds = new NetIdAllocator("NetIds", registry.Runtime);
            var state = new ArchetypeReplicationState("Creature", registry.Runtime, engine.MemoryAllocator, plan.BlockLayout,
                new SubscriptionsOptions { StatePoolBudgetBytes = AmpleBudget }, netIds);

            var harness = new Harness
            {
                Engine = engine,
                Plan = plan,
                State = state,
                NetIds = netIds,
                _registry = registry,
                ClusterState = engine._archetypeStates[plan.ArchetypeCatalogId].ClusterState,
            };

            harness.Spawn(entities, spread);
            return harness;
        }

        public void Spawn(int count, float spread = 0f)
        {
            using var tx = Engine.CreateQuickTransaction();
            for (var i = 0; i < count; i++)
            {
                var x = spread <= 0f ? 10f : 10f + ((i % 64) * spread);
                var y = spread <= 0f ? 10f : 10f + ((i / 64) * spread);
                var bounds = At(x, y);
                var ai = new ProjAi { Template = 7, Level = 100, Mode = ProjAiMode.Idle };
                var vitals = new ProjVitals { Health = 5, MaxHealth = 10 };
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            }

            tx.Commit();
        }
    }

    private static ProjBounds At(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f }, Speed = 1f };

    /// <summary>Writes one field of one slot through the span path, which sets no dirty bit and signals nothing.</summary>
    private static void WriteLevelThroughGetSpan(Harness harness, int slot, ushort level)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
#pragma warning disable TYPHON009
            var ai = cluster.GetSpan(ProjCreature.Ai);
#pragma warning restore TYPHON009
            ai[slot].Level = level;
            break;
        }

        accessor.Dispose();
        tx.Commit();
    }

    // ── The pass's own properties ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An entity nobody changed costs its comparison and produces nothing.</summary>
    [Test]
    public void AnUnchangedEntityProducesNoRecord()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);

        harness.Tick(1);
        Assert.That(harness.Changed, Is.EqualTo(8), "every entity enters on the tick it is first watched");

        harness.Tick(2);
        Assert.That(harness.Changed, Is.Zero, "nothing changed, so nothing is encoded");

        harness.Tick(3);
        Assert.That(harness.Changed, Is.Zero);
    }

    /// <summary>A change confined to one group stamps that group's tick and leaves every other group's where it was.</summary>
    [Test]
    public void AChangeInOneGroupStampsOnlyThatGroupsTick()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);
        harness.Tick(1);
        harness.Tick(2);

        var stateSlot = harness.TickSlotOf("state");
        var vitalsSlot = harness.TickSlotOf("vitals");
        WriteLevelThroughGetSpan(harness, slot: 3, level: 4242);
        harness.Tick(3);

        var hot = harness.Hot(harness.FirstBlock, 3);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Changed, Is.EqualTo(1), "one entity changed");
            Assert.That(harness.Entered(3), Is.False, "a change, not an enter");
            Assert.That(hot->Flags & ProjectionPass.FlagInitializedThisTick, Is.Zero);
            Assert.That(hot->GroupTicks[vitalsSlot], Is.EqualTo(3u), "'level' is in the vitals group, whose tick moves");
            Assert.That(hot->GroupTicks[stateSlot], Is.EqualTo(1u), "the state group did not change, so its tick stays at the enter");
        });
    }

    /// <summary>
    /// An entity projected again after ticks nobody pushed it is NOT re-initialized: what a client holds is geometry, not a per-session history, so an
    /// entity's entry stays valid however long it goes unpushed.
    /// </summary>
    [Test]
    public void AnEntityProjectedAfterAGapKeepsItsEntry()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);
        harness.Tick(1);
        harness.Tick(2);
        Assert.That(harness.Changed, Is.Zero, "the quiet tick is quiet");

        var before = harness.Hot(harness.FirstBlock, 0)->NetId;

        // Tick 3 is skipped entirely — nobody pushed these entities — and tick 4 finds every entry as tick 2 left it.
        harness.Tick(4);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Changed, Is.Zero, "nothing changed, so nothing enters again");
            Assert.That(harness.Entered(0), Is.False);
            Assert.That(harness.Hot(harness.FirstBlock, 0)->NetId, Is.EqualTo(before), "the entity never left, so its identity is kept");
        });
    }

    /// <summary>A slot the engine no longer counts as occupied gives its identity back, with no destroy hook anywhere.</summary>
    [Test]
    public void ASlotWhoseOccupancyBitClearedReleasesItsNetId()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);
        harness.Tick(1);
        Assert.That(harness.NamedEntities, Is.EqualTo(8));

        var block = harness.FirstBlock;
        var occupancyBefore = *(ulong*)ClusterBase(harness, block->ChunkId);

        DestroyOne(harness);

        // Marked with the mask the push path would have carried in, not with the live one: the point of the case is an entry whose slot went away under
        // it.
        harness.State.WatchedBlocks.ClearMasks();
        harness.State.BeginWatchedBlocks(2);
        harness.MarkSlots(block, occupancyBefore);
        harness.Project(2);

        Assert.Multiple(() =>
        {
            Assert.That(harness.State.IdentitiesReleased, Is.EqualTo(1), "exactly the destroyed entity's identity");
            Assert.That(harness.State.NetIdLeases.PendingReleases, Is.EqualTo(1), "queued, because the allocator has one writer");
        });

        // The next tick's blocks step is what hands the release to the allocator.
        harness.State.BeginProjectTick(workers: 1);
        Assert.That(harness.NamedEntities, Is.EqualTo(7));
    }

    /// <summary>A slot handed to a different entity is detected by the entry's own <c>EntityId</c>, and the previous holder's identity is released.</summary>
    [Test]
    public void AnEntityIdMismatchReinitializesAndReleasesTheStaleIdentity()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);
        harness.Tick(1);

        var block = harness.FirstBlock;
        var identitiesBefore = new uint[64];
        for (var slot = 0; slot < harness.Plan.SlotCount; slot++)
        {
            identitiesBefore[slot] = harness.Hot(block, slot)->NetId;
        }

        DestroyOne(harness);
        harness.Spawn(1);

        harness.Tick(2);

        var reused = -1;
        for (var slot = 0; slot < harness.Plan.SlotCount; slot++)
        {
            var netId = harness.Hot(block, slot)->NetId;
            if (netId != 0 && identitiesBefore[slot] != 0 && netId != identitiesBefore[slot])
            {
                reused = slot;
                break;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(reused, Is.GreaterThanOrEqualTo(0), "the respawn took a slot whose entry described the entity that left");
            Assert.That(harness.State.IdentitiesReleased, Is.GreaterThanOrEqualTo(1), "the stale identity went back");
            Assert.That(harness.Changed, Is.GreaterThanOrEqualTo(1));
            Assert.That(harness.Entered(reused), Is.True, "a reused slot is a new entity, so it enters");
        });
    }

    // ── SUB-10 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// SUB-10 — a component written through <see cref="ClusterRef{TArch}.GetSpan{T}"/> is detected, and it sets no dirty bit.
    /// </summary>
    /// <remarks>
    /// <c>GetSpan</c> is "the one write path that signals nothing" (<c>ClusterRef.cs:157-162</c>): it marks no slot dirty and raises no modified flag. A
    /// change detector keyed on any write-path signal therefore reports nothing here, and the client's world silently stops being the server's. What this
    /// asserts is that the pass noticed anyway — because it compared the quantized value with the one it had stored, and for no other reason.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-10")]
    public void AWriteThroughGetSpanIsDetected()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);
        harness.Tick(1);
        harness.Tick(2);
        Assert.That(harness.Changed, Is.Zero, "the baseline: an unchanged tick produces nothing, so the record below is the write and not noise");

        WriteLevelThroughGetSpan(harness, slot: 2, level: 777);
        harness.Tick(3);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Changed, Is.EqualTo(1), "the un-signalled write was found by the comparison");
            Assert.That(harness.Entered(2), Is.False, "as a change, not an enter");
            Assert.That(harness.Hot(harness.FirstBlock, 2)->GroupTicks[harness.TickSlotOf("vitals")], Is.EqualTo(3u),
                "and it is the group the written field belongs to whose tick moved");
        });
    }

    /// <summary>
    /// SUB-10 — a position written through <see cref="ClusterRef{TArch}.WriteSpatial{T}"/> is detected, and it marks no slot dirty either.
    /// </summary>
    /// <remarks>
    /// The second of the two write paths the rule names (<c>ClusterRef.cs:342-348</c>), and the one that carries the traffic: an entity's position is written
    /// this way every tick precisely so the WAL is not flooded, which is exactly why replication cannot ask the WAL's bookkeeping what moved.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-10")]
    public void AWriteThroughWriteSpatialIsDetected()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);
        harness.Tick(1);
        harness.Tick(2);

        var motionTick = harness.Hot(harness.FirstBlock, 1)->GroupTicks[0];
        Assert.That(motionTick, Is.EqualTo(1u), "the motion tick was stamped at the enter and has not moved since");

        MoveOneThroughWriteSpatial(harness, slot: 1, dx: 3.5f);
        harness.Tick(3);

        var hot = harness.Hot(harness.FirstBlock, 1);
        Assert.Multiple(() =>
        {
            Assert.That(hot->GroupTicks[0], Is.EqualTo(3u), "the motion tick moved, so the quantized position was compared and found different");
            Assert.That(hot->Flags & ProjectionPass.FlagPositionChanged, Is.Not.Zero);
        });
    }

    /// <summary>
    /// The falsifiability of both SUB-10 cases: if the pass is not run, the assertion they rest on fails. A verifier that would pass without the pass having
    /// looked at the value would be asserting nothing.
    /// </summary>
    [Test]
    [RuleMutant("SUB-10")]
    public void AChangeNoPassEverLooksAtIsNotDetected()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);
        harness.Tick(1);
        harness.Tick(2);

        var before = harness.Hot(harness.FirstBlock, 2)->GroupTicks[harness.TickSlotOf("vitals")];
        WriteLevelThroughGetSpan(harness, slot: 2, level: 777);
        MoveOneThroughWriteSpatial(harness, slot: 1, dx: 3.5f);

        // Deliberately NOT projecting. Nothing the engine does on a write path tells replication anything, which is the premise both verifiers rest on: with
        // no comparison run, the entry is exactly what it was.
        Assert.Multiple(() =>
        {
            Assert.That(harness.Hot(harness.FirstBlock, 2)->GroupTicks[harness.TickSlotOf("vitals")], Is.EqualTo(before),
                "no write path stamped anything, so the verifier's assertion discriminates the pass from the write");
            Assert.That(harness.Hot(harness.FirstBlock, 1)->GroupTicks[0], Is.EqualTo(1u));
            Assert.That(harness.Changed, Is.Zero);
        });
    }

    // ── SUB-13 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// SUB-13 — per-tick work is bounded by the pushed set, never by the archetype.
    /// </summary>
    /// <remarks>
    /// An archetype of N entities across many clusters, with W of them pushed, and a count of the slots the pass addressed. The count is exact rather than
    /// asymptotic — the pass either touched a slot or it did not — which is what makes a regression to a whole-archetype walk impossible to miss.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-13")]
    public void PerTickWorkFollowsThePushedSet()
    {
        const int entities = 1024;
        using var harness = Harness.Create(ServiceProvider, entities, spread: 40f);

        var (chunkId, occupancy) = FirstPopulatedCluster(harness);
        Assert.That(harness.State.TryAttachBlock(chunkId, out var block), Is.True);

        // One cluster's worth of entities is pushed; every other entity of the archetype is not.
        harness.State.BeginWatchedBlocks(1);
        harness.MarkSlots(block, occupancy);
        var watched = BitOperations.PopCount(occupancy);

        harness.State.ResetProjectionCounters();
        harness.Project(1);

        Assert.Multiple(() =>
        {
            Assert.That(harness.State.SlotsProjected, Is.EqualTo(watched), "the pass addressed exactly the pushed slots");
            Assert.That(harness.State.BlocksProjected, Is.EqualTo(1), "and exactly the pushed block");
            Assert.That(harness.State.SlotsProjected * 4, Is.LessThan(entities), "which is a small fraction of an archetype it never walked");
        });
    }

    /// <summary>
    /// The falsifiability of the SUB-13 case: a pass driven over every cluster of the archetype visits every entity, and the assertion above rejects it.
    /// </summary>
    [Test]
    [RuleMutant("SUB-13")]
    public void AWholeArchetypeWalkIsDetected()
    {
        const int entities = 1024;
        using var harness = Harness.Create(ServiceProvider, entities, spread: 40f);

        harness.State.ResetProjectionCounters();
        harness.Tick(1);

        var visited = harness.State.SlotsProjected;
        Assert.Multiple(() =>
        {
            Assert.That(visited, Is.EqualTo(entities), "marking the whole archetype makes the pass cost the whole archetype");
            Assert.That(harness.State.BlocksProjected, Is.GreaterThan(1), "over more than one cluster");
            Assert.That(visited * 4, Is.GreaterThanOrEqualTo(entities),
                "so PerTickWorkFollowsThePushedSet's assertion would reject this — the verifier measures the pushed set and not the pass's mere existence");
        });
    }

    // ── Per-entity, per-tick, never per-session ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Over a thousand ticks, a change is found once per changed entity per tick — and the count is a function of the changes alone.
    /// </summary>
    /// <remarks>
    /// "Never once per session" is a property of the pass's SHAPE rather than of a number: no session is reachable from
    /// <see cref="ProjectionPass.ProjectBlock"/>, so the totals below cannot vary with how many sessions are connected. What the thousand ticks add is that
    /// the count does not drift — an entry that compared unequal against itself would show up here as a change per entity per tick.
    /// </remarks>
    [Test]
    public void ChangesAreFoundOncePerChangedEntityPerTick()
    {
        const int entities = 64;
        const uint ticks = 1000;
        using var harness = Harness.Create(ServiceProvider, entities);

        harness.Tick(1);
        Assert.That(harness.State.RecordsProduced, Is.EqualTo(entities), "the first tick is one enter per entity");

        var changed = 0L;
        for (var tick = 2u; tick <= ticks; tick++)
        {
            // One entity changes per tick, cycling, so the stored copy of every other entity has to compare equal for the whole run.
            WriteLevelThroughGetSpan(harness, slot: (int)(tick % 8), level: (ushort)(1000 + tick));
            changed++;
            harness.Tick(tick);
        }

        Assert.That(harness.State.RecordsProduced, Is.EqualTo(entities + changed),
            "one enter per entity, then exactly one record per changed entity per tick and nothing else");
    }

    /// <summary>
    /// SUB-07's shape for this pass: once the push set is stable, a projection tick allocates no managed memory at all.
    /// </summary>
    /// <remarks>
    /// The measured region is the pass and nothing else — the accessor, the epoch scope and the marking are taken outside it, because they belong to the
    /// stage rather than to S1. Everything the pass touches is native (the block, the code and body scratch) or on the stack.
    /// </remarks>
    [Test]
    public void TheSteadyStatePassAllocatesNoManagedMemory()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);

        for (var tick = 1u; tick <= 8; tick++)
        {
            harness.Tick(tick);
        }

        harness.MarkAllWatched(9);
        harness.State.BeginProjectTick(workers: 1);

        using var guard = EpochGuard.Enter(harness.Engine.EpochManager);
        using var accessor = harness.ClusterState.ClusterSegment.CreateChunkAccessor();
        var block = harness.State.WatchedBlocks[0];
        var clusterBase = accessor.GetChunkAddress(block->ChunkId);

        // Warm-up inside the measured shape, so the JIT and every native buffer have reached their steady size before the delta is taken.
        for (var i = 0; i < 64; i++)
        {
            ProjectionPass.ProjectBlock(harness.Plan, 0, harness.State, 0, block, clusterBase, null, 9);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            ProjectionPass.ProjectBlock(harness.Plan, 0, harness.State, 0, block, clusterBase, null, 9);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.Zero, $"a steady-state projection allocated {allocated} managed bytes");
    }

    /// <summary>What an entry costs for the SWG creature shape: the state bodies the hot entry reserves, and the entry strides.</summary>
    /// <remarks>
    /// Stated as a test rather than in prose because it is the number AC-13's byte budget is built on, and because every part of it is a consequence of the
    /// compiled plan: change a codec and this moves.
    /// </remarks>
    [Test]
    public void AnEntryIsAsBigAsTheCompiledPlanMakesIt()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 8);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Plan.MaxStateBodyBytes, Is.EqualTo(4), "the packed 'state' group and 'vitals' as u16 + unorm8");
            Assert.That(harness.Layout.HotStride, Is.EqualTo(64), "32 fixed + a 14 B segment + a 4 B state body still fits one cache line (AC-5)");
            Assert.That(harness.Layout.ColdStride, Is.EqualTo(32));
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static byte* ClusterBase(Harness harness, int chunkId)
    {
        using var guard = EpochGuard.Enter(harness.Engine.EpochManager);
        using var accessor = harness.ClusterState.ClusterSegment.CreateChunkAccessor();
        return accessor.GetChunkAddress(chunkId);
    }

    private static (int ChunkId, ulong Occupancy) FirstPopulatedCluster(Harness harness)
    {
        using var guard = EpochGuard.Enter(harness.Engine.EpochManager);
        using var accessor = harness.ClusterState.ClusterSegment.CreateChunkAccessor();
        var ids = harness.ClusterState.ReadActiveClusterList(out var count);
        for (var i = 0; i < count; i++)
        {
            var chunkId = ids[i];
            if (chunkId < 0)
            {
                continue;
            }

            var occupancy = *(ulong*)accessor.GetChunkAddress(chunkId);
            if (occupancy != 0)
            {
                return (chunkId, occupancy);
            }
        }

        throw new InvalidOperationException("the fixture spawned nothing");
    }

    private static void DestroyOne(Harness harness)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var victim = EntityId.Null;
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            victim = cluster.GetEntityId(BitOperations.TrailingZeroCount(bits));
            break;
        }

        accessor.Dispose();
        Assert.That(victim.IsNull, Is.False, "the fixture holds an entity to destroy");
        tx.Destroy(victim);
        tx.Commit();
    }

    private static void MoveOneThroughWriteSpatial(Harness harness, int slot, float dx)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var current = cluster.GetReadOnlySpan(ProjCreature.Bounds)[slot];
            var moved = new ProjBounds
            {
                Bounds = new AABB2F
                {
                    MinX = current.Bounds.MinX + dx,
                    MinY = current.Bounds.MinY,
                    MaxX = current.Bounds.MaxX + dx,
                    MaxY = current.Bounds.MaxY,
                },
                Speed = current.Speed,
            };

            cluster.WriteSpatial(ProjCreature.Bounds, slot, in moved);
            break;
        }

        accessor.Dispose();
        tx.Commit();
    }
}
