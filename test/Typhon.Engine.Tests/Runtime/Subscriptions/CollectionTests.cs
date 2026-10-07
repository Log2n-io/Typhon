using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>An item: text, a reference, a float and two integers — what a collection's element may hold (W34, E-7).</summary>
[StructLayout(LayoutKind.Sequential)]
struct ProjItem
{
    public int Id;
    public String64 Name;
    public EntityLink<ProjBagged> Owner;
    public float Weight;
    public ushort Stack;
}

/// <summary>An element with a <see cref="bool"/>, whose marshalled width is not its width in a buffer: refused.</summary>
[StructLayout(LayoutKind.Sequential)]
struct ProjFlagged
{
    public int Id;
    public bool Lit;
}

[Component("Typhon.Test.Proj.Bag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ProjBag
{
    [Field]
    public ComponentCollection<ProjItem> Items;

    [Field]
    public ComponentCollection<ProjFlagged> Flags;

    [Field]
    public int Level;
}

[Archetype]
partial class ProjBagged : Archetype<ProjBagged>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjBag> Bag = Register<ProjBag>();
}

/// <summary>
/// Collections on entity fields (W34, 13 § 6.5, E-7): a <c>ComponentCollection&lt;T&gt;</c> replicates in a public group and in the owner section, its
/// elements' text and references included; a longer one is cut at <c>maxCount</c>, counted, and the client sees its total; a change re-sends it whole;
/// a reference in an element follows its target's identity; and the arena returns to its start after churn.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class CollectionTests : TestBase<CollectionTests>
{
    private const string Profile = "world";

    private static DatabaseEngine Engine(IServiceProvider services)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjBag>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new System.Numerics.Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
            worldMax: new System.Numerics.Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM),
            cellSize: 256f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjBagged>(a => a
            .Motion(ProjBagged.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjBagged.Bag, x => x.Items, Codec.Coll(4), name: "items", group: "bag")
            .Field(ProjBagged.Bag, x => x.Level, Codec.Exact, name: "level", group: "bag")
            .Owner(o => o.Field(ProjBagged.Bag, x => x.Items, Codec.Coll(2), name: "mine")));
        subs.Profile(Profile, p => p.World().Of<ProjBagged>());
    }

    private static ProjBounds At(float x) => new() { Bounds = new AABB2F { MinX = x - 0.5f, MinY = -0.5f, MaxX = x + 0.5f, MaxY = 0.5f }, Speed = 1f };

    private static ProjItem Item(int id, string name, EntityId owner = default, float weight = 1.5f, ushort stack = 1) =>
        new() { Id = id, Name = name, Owner = owner, Weight = weight, Stack = stack };

    private static EntityId Spawn(DatabaseEngine dbe, float x, params ProjItem[] items)
    {
        using var tx = dbe.CreateQuickTransaction();
        var bounds = At(x);
        var bag = new ProjBag { Level = 1 };
        using (var accessor = tx.CreateComponentCollectionAccessor(ref bag.Items))
        {
            foreach (var item in items)
            {
                accessor.Add(item);
            }
        }

        var id = tx.Spawn<ProjBagged>(ProjBagged.Bounds.Set(in bounds), ProjBagged.Bag.Set(in bag));
        tx.Commit();
        return id;
    }

    // Appends to an entity's collection and pushes it, as an explicit profile's system does (ADR-067).
    private static void Append(FrameHarness harness, EntityId id, params ProjItem[] items)
    {
        using (var tx = harness.Engine.CreateQuickTransaction())
        {
            var opened = tx.OpenMut(id);
            var bag = opened.Read(ProjBagged.Bag);
            using (var accessor = opened.CreateComponentCollectionAccessor(ProjBagged.Bag, ref bag, ref bag.Items))
            {
                foreach (var item in items)
                {
                    accessor.Add(item);
                }
            }
            opened.Set(ProjBagged.Bag, bag);

            tx.Commit();
        }

        Push(harness, id);
    }

    private static void Push(FrameHarness harness, EntityId id)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjBagged>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                if (cluster.GetEntityId(slot) == id)
                {
                    harness.Subscriptions.Commands.Replicate(in cluster, slot);
                }
            }
        }

        tx.Commit();
    }

    private static void Destroy(DatabaseEngine dbe, params EntityId[] ids)
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in ids)
        {
            tx.Destroy(id);
        }

        tx.Commit();
    }

    private (FrameHarness Harness, SessionId Session, int Archetype) Start(DatabaseEngine dbe)
    {
        var harness = FrameHarness.Create(dbe, Declare, nameof(CollectionTests), replicationCellM: ProjectionTestSchema.ReplicationCellFor(0));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, Profile)[0];
        return (harness, session, harness.CatalogPlan.ArchetypeByName(nameof(ProjBagged)).Idx);
    }

    private static Typhon.Client.CollectionValue Items(FrameHarness harness, SessionId session, int archetype, EntityId entity) =>
        harness.Replica(session).Collection(archetype, harness.NetIdOf(entity), "items");

    [Test]
    [VerifiesRule("SUB-01")]
    public void ACollectionReachesTheClientInAGroupAndInTheOwnerSection()
    {
        var dbe = Engine(ServiceProvider);
        var anchor = Spawn(dbe, 10f);
        var (harness, session, archetype) = Start(dbe);
        using var _ = harness;
        harness.RunTick(1);
        var bag = Spawn(harness.Engine, 20f, Item(1, "épée", anchor, 2.5f, 3), Item(2, "", default, -0.0f, 65535));
        Assert.That(harness.Sessions.SetControlled(session, bag), Is.True);
        harness.RunTick(2);
        harness.RunTick(3);
        harness.Deliver(session);

        var items = Items(harness, session, archetype, bag);
        var mine = harness.Replica(session).Store.Self.Collections[0];
        Assert.Multiple(() =>
        {
            Assert.That((items.Total, items.Count), Is.EqualTo((2, 2)));
            Assert.That(items.Text(0, "Name"), Is.EqualTo("épée"));
            Assert.That(items.Number(0, "Id"), Is.EqualTo(1));
            Assert.That(items.Number(0, "Owner"), Is.EqualTo(harness.NetIdOf(anchor)), "an EntityLink in an element is its target's netId");
            Assert.That(items.Number(0, "Weight"), Is.EqualTo(2.5));
            Assert.That(items.Number(1, "Stack"), Is.EqualTo(65535));
            Assert.That(items.Number(1, "Owner"), Is.Zero, "a null reference");
            Assert.That((mine.Total, mine.Count), Is.EqualTo((2, 2)), "the owner section's collection, to the controlling session");
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
        });
    }

    [Test]
    public void ALongerCollectionIsCutAtMaxCountCountedAndVisible()
    {
        var dbe = Engine(ServiceProvider);
        var (harness, session, archetype) = Start(dbe);
        using var _ = harness;
        harness.RunTick(1);
        var bag = Spawn(harness.Engine, 20f, Enumerable.Range(1, 6).Select(i => Item(i, $"n{i}")).ToArray());
        harness.RunTick(2);
        harness.Deliver(session);

        var plan = harness.Subscriptions.Plans[harness.PlanIndex(nameof(ProjBagged))];
        var state = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjBagged))];
        var items = Items(harness, session, archetype, bag);
        Assert.Multiple(() =>
        {
            Assert.That((items.Total, items.Count, items.Truncated), Is.EqualTo((6, 4, true)), "four sent, six held, and the client knows");
            Assert.That(Enumerable.Range(0, 4).Select(i => items.Number(i, "Id")), Is.EqualTo(new double[] { 1, 2, 3, 4 }));
            Assert.That(state.ClampsOf(Array.FindIndex(plan.Fields, f => f.Name == "items")), Is.EqualTo(1), "the cut is counted on the field");
        });
    }

    [Test]
    public void AChangedCollectionIsResentWholeAndAnUnchangedPushSendsNothing()
    {
        var dbe = Engine(ServiceProvider);
        var (harness, session, archetype) = Start(dbe);
        using var _ = harness;
        harness.RunTick(1);
        var bag = Spawn(harness.Engine, 20f, Item(1, "one"));
        harness.RunTick(2);
        harness.Deliver(session);

        Push(harness, bag);
        harness.RunTick(3);
        Assert.That(harness.Deliver(session), Is.Zero, "a push that changed no byte sends no record (SUB-10)");

        Append(harness, bag, Item(2, "two"), Item(3, "three"));
        harness.RunTick(4);
        Assert.That(harness.Deliver(session), Is.EqualTo(1));
        var items = Items(harness, session, archetype, bag);
        Assert.That(Enumerable.Range(0, items.Count).Select(i => items.Text(i, "Name")), Is.EqualTo(new[] { "one", "two", "three" }));
    }

    [Test]
    [VerifiesRule("SUB-31")]
    public void AnElementsReferenceFollowsItsTargetAndCountsPerOccurrence()
    {
        var dbe = Engine(ServiceProvider);
        var target = Spawn(dbe, 10f);
        var (harness, session, archetype) = Start(dbe);
        using var _ = harness;
        harness.RunTick(1);
        var bag = Spawn(harness.Engine, 20f, Item(1, "a", target), Item(2, "b", target), Item(3, "c"));
        harness.RunTick(2);
        harness.RunTick(3);
        harness.Deliver(session);

        var index = harness.Subscriptions.References;
        var held = harness.NetIdOf(target);
        Assert.Multiple(() =>
        {
            Assert.That(Items(harness, session, archetype, bag).Number(1, "Owner"), Is.EqualTo(held));
            Assert.That(index.CountOf(held, bag), Is.EqualTo(4), "two elements name it, in the public collection and again in the owner one");
        });

        // The bag is neither written nor pushed by the application: its target's release is what pushes it.
        Destroy(harness.Engine, target);
        harness.RunTick(4);
        harness.Deliver(session);
        harness.RunTick(5);
        harness.Deliver(session);
        harness.RunTick(6);
        Assert.Multiple(() =>
        {
            var items = Items(harness, session, archetype, bag);
            Assert.That(new[] { items.Number(0, "Owner"), items.Number(1, "Owner") }, Is.EqualTo(new double[] { 0, 0 }));
            Assert.That(index.PairCount, Is.Zero);
            Assert.That(index.DroppedDecrements, Is.Zero, "the re-projection's −1s were for a netId just taken: expected");
            Assert.That(harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjBagged))].CollectionDecodeFaults, Is.Zero);
        });
    }

    [Test]
    public void TheArenaAndTheIndexHoldNothingNoEntryNamesAfterChurn() => Churn(200);

    // E-7 at its stated length. The same cases as the 200-cycle test, longer: the gate runs that one, this one the nightly tier.
    [Test]
    [Explicit("E-7's full length, 10 000 churn cycles — about 2 s, past the unit-test budget; the gate runs the 200-cycle form")]
    [Category("Nightly")]
    [CancelAfter(300_000)]
    public void TheArenaAndTheIndexHoldNothingNoEntryNamesAfterTenThousandChurnCycles() => Churn(10_000);

    // Waves of four bags, each naming the keeper twice, spawned and destroyed: afterwards only the keeper's bodies are live, no pair names a dead
    // referrer, and no −1 went unmatched.
    private void Churn(int cycles)
    {
        var dbe = Engine(ServiceProvider);
        var keeper = Spawn(dbe, 10f);
        var (harness, session, _) = Start(dbe);
        using var _h = harness;
        var state = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjBagged))];
        var tick = 1L;
        harness.RunTick(tick++);
        harness.RunTick(tick++);
        var start = state.WideBodies.LiveBodies;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var wave = Enumerable.Range(0, 4).Select(i => Spawn(harness.Engine, 20f + i, Item(i, $"w{cycle}", keeper), Item(i + 1, "x", keeper))).ToArray();
            harness.RunTick(tick++);
            Destroy(harness.Engine, wave);
            harness.RunTick(tick++);
            harness.Deliver(session);
        }

        harness.RunTick(tick++);
        Assert.Multiple(() =>
        {
            Assert.That(state.WideBodies.LiveBodies, Is.EqualTo(start), "only the keeper's bodies are live");
            Assert.That(harness.Subscriptions.References.PairCount, Is.Zero, "the dead referrers' counts went with their entries");
            Assert.That(harness.Subscriptions.References.DroppedDecrements, Is.Zero);
        });
    }

    // Lists past one read batch (64 elements) and past one buffer chunk, on several projection worker lists, then all destroyed at once: every element
    // arrives in order, and every pair the elements made goes with its entry.
    [Test]
    public void ALongCollectionCrossesBatchesAndChunksOnSeveralWorkers()
    {
        const int Elements = 150;
        const int Bags = 24;
        var dbe = Engine(ServiceProvider);
        var keeper = Spawn(dbe, 10f);
        var harness = FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<ProjBagged>(a => a
                .Motion(ProjBagged.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
                .Field(ProjBagged.Bag, x => x.Items, Codec.Coll(Elements), name: "items", group: "bag"));
            subs.Profile(Profile, p => p.World().Of<ProjBagged>());
        }, nameof(ALongCollectionCrossesBatchesAndChunksOnSeveralWorkers), replicationCellM: ProjectionTestSchema.ReplicationCellFor(0));
        using var _ = harness;
        harness.RunFence = true;
        harness.ProjectionWorkers = 4;
        var session = harness.OpenSessions(1, Profile)[0];
        var archetype = harness.CatalogPlan.ArchetypeByName(nameof(ProjBagged)).Idx;
        harness.RunTick(1, workers: 4);
        var bags = Enumerable.Range(0, Bags)
            .Select(b => Spawn(harness.Engine, 20f + b, Enumerable.Range(0, Elements).Select(i => Item(i, $"e{i}", keeper)).ToArray()))
            .ToArray();
        harness.RunTick(2, workers: 4);
        harness.RunTick(3, workers: 4);
        harness.Deliver(session);

        var index = harness.Subscriptions.References;
        var held = harness.NetIdOf(keeper);
        Assert.Multiple(() =>
        {
            foreach (var bag in bags)
            {
                var items = Items(harness, session, archetype, bag);
                Assert.That((items.Total, items.Count), Is.EqualTo((Elements, Elements)));
                Assert.That(Enumerable.Range(0, Elements).All(i => items.Number(i, "Id") == i && items.Text(i, "Name") == $"e{i}"), Is.True);
                Assert.That(items.Number(Elements - 1, "Owner"), Is.EqualTo(held));
                Assert.That(index.CountOf(held, bag), Is.EqualTo(Elements), "one per element naming it");
            }
        });

        Destroy(harness.Engine, bags);
        harness.RunTick(4, workers: 4);
        harness.RunTick(5, workers: 4);
        Assert.Multiple(() =>
        {
            Assert.That(index.PairCount, Is.Zero, "every pair went with its entry, whichever worker ended it");
            Assert.That(index.DroppedDecrements, Is.Zero);
            Assert.That(harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjBagged))].CollectionDecodeFaults, Is.Zero);
        });
    }

    // E-15 (SUB-07): text, references and collections, re-encoded and stored again every tick, allocate nothing once the buffers have their size. Every
    // bag's level changes each tick, and the level shares its group with the collection, so the whole group — elements, their text, their references —
    // is encoded, compared and rewritten into its wide body each tick. Measured: the projection and the frames — SUB-07's stages — and nothing the harness
    // does around them on the same thread: the application's writes, the fence (which the runtime runs with the tick's change set, the harness without),
    // and the send side, whose ClearReady the runtime's pump calls each tick and the harness has to call itself or the ready list grows.
    [Test]
    [VerifiesRule("SUB-07")]
    public void ASteadyStateTickWithTextReferencesAndCollectionsAllocatesNothing()
    {
        var dbe = Engine(ServiceProvider);
        var keeper = Spawn(dbe, 10f);
        var (harness, session, archetype) = Start(dbe);
        using var _ = harness;
        harness.RunFence = false;
        var bags = Enumerable.Range(0, 8).Select(b => Spawn(harness.Engine, 20f + b, Item(1, "épée", keeper), Item(2, "sword", keeper), Item(3, "s")))
            .ToArray();
        var allocated = 0L;
        const int Warm = 100;
        const int Measured = 200;
        for (var tick = 1L; tick <= Warm + Measured; tick++)
        {
            using (var tx = harness.Engine.CreateQuickTransaction())
            {
                foreach (var bag in bags)
                {
                    var entity = tx.OpenMut(bag);
                    var bagCopy = entity.Read(ProjBagged.Bag);
                    bagCopy.Level = (int)tick;
                    entity.Set(ProjBagged.Bag, bagCopy);
                }

                tx.Commit();
            }

            foreach (var bag in bags)
            {
                Push(harness, bag);
            }

            harness.Engine.WriteTickFence(tick);
            var before = GC.GetAllocatedBytesForCurrentThread();
            harness.RunUpToFrames(tick);
            harness.RunFramesOnly(tick);
            if (tick > Warm)
            {
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            harness.Assembler.Gate.Publish(tick);
            harness.Assembler.ClearReady();
            harness.Deliver(session);
        }

        var items = Items(harness, session, archetype, bags[^1]);
        Assert.Multiple(() =>
        {
            Assert.That((items.Count, items.Text(0, "Name"), items.Number(1, "Owner")), Is.EqualTo((3, "épée", (double)harness.NetIdOf(keeper))),
                "precondition: the bags replicated, text and references included");
            Assert.That(allocated, Is.Zero, $"{Measured} steady ticks allocated {allocated} managed bytes");
        });
    }

    [Test]
    public void ACollectionDeclaredWrongIsRefused()
    {
        var dbe = Engine(ServiceProvider);
        void Refused(string why, Action<ArchetypeProjectionBuilder> declare) =>
            Assert.That(Assert.Throws(Is.InstanceOf<Exception>(), () => FrameHarness.Create(dbe, subs =>
            {
                subs.Archetype<ProjBagged>(a => declare(a.Motion(ProjBagged.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))));
                subs.Profile(Profile, p => p.World().Of<ProjBagged>());
            }, why))!.Message, Does.Contain(why));

        Refused("Codec.Coll(maxCount)", a => a.Field(ProjBagged.Bag, x => x.Items, Codec.Exact, name: "items", group: "bag"));
        Refused("a collection is a ComponentCollection<T> field", a => a.Field(ProjBagged.Bag, x => x.Level, Codec.Coll(4), name: "level", group: "bag"));
        Refused("marshalled width", a => a.Field(ProjBagged.Bag, x => x.Flags, Codec.Coll(4), name: "flags", group: "bag"));
        Refused("SUB-31", a => a.OnEnter(ProjBagged.Bag, x => x.Items, Codec.Coll(4), name: "items"));
    }
}
