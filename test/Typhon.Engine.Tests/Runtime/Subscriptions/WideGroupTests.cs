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

/// <summary>A label: text a public group and a truncated copy carry (13 § 2.1, E-4).</summary>
[Component("Typhon.Test.Proj.Label", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjLabel
{
    [Field]
    public String64 Name;
}

/// <summary>A tag sent once, on enter.</summary>
[Component("Typhon.Test.Proj.Tag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjTag
{
    [Field]
    public String64 Tag;
}

/// <summary>
/// A biography, in the owner section. A <see cref="String64"/>: a <see cref="String1024"/> cannot be in a replicated archetype at all, since cluster
/// storage needs eight entities to a page and eight kilobytes of text do not fit one.
/// </summary>
[Component("Typhon.Test.Proj.Bio", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjBio
{
    [Field]
    public String64 Bio;

    [Field]
    public int Level;
}

[Archetype]
partial class ProjLabelled : Archetype<ProjLabelled>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjLabel> Label = Register<ProjLabel>();
    public static readonly Comp<ProjTag> Tag = Register<ProjTag>();
    public static readonly Comp<ProjBio> Bio = Register<ProjBio>();
}

/// <summary>The text pairings (13 § 2.3): a string field's exact codec is its capacity less its terminator, and only <c>str</c> carries it.</summary>
[TestFixture]
public class TextPairingTests
{
    [TestCase(typeof(String64), 63, null)]
    [TestCase(typeof(String1024), 1023, null)]
    [TestCase(typeof(Variant), 63, "variant")]
    public void ExactTextIsTheCapacityLessItsTerminator(Type source, int maxBytes, string shape)
    {
        var resolved = CodecPairing.Resolve(source, Codec.Exact, "test", out var resolvedShape);
        Assert.Multiple(() =>
        {
            Assert.That(resolved.Catalog.Kind, Is.EqualTo(CodecKind.Str));
            Assert.That(resolved.Catalog.MaxBytes, Is.EqualTo(maxBytes));
            Assert.That(resolvedShape, Is.EqualTo(shape));
            Assert.That(CodecPairing.Classify(source, resolved.Catalog, false, "test"), Is.EqualTo(ColumnPath.Text));
        });
    }

    [Test]
    public void TextRefusesACapAboveItsCapacityAndEveryOtherCodec()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Codec.Declared<String64>(CodecKind.Unknown).ToString(), Is.EqualTo("str{63}"), "a bare [Replicate] on a String64");
            Assert.That(CodecPairing.Classify(typeof(String64), Codec.Str(10).Catalog, false, "test"), Is.EqualTo(ColumnPath.Text), "a smaller cap");
            Assert.That(Assert.Throws<InvalidOperationException>(() => CodecPairing.Classify(typeof(String64), Codec.Str(64).Catalog, false, "f")).Message,
                Does.Contain("at most 63"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => CodecPairing.Classify(typeof(String1024), Codec.U8.Catalog, false, "f")).Message,
                Does.Contain("text travels as str"));
        });
    }

    [Test]
    public void TheLongestValidPrefixEndsOnACodePoint()
    {
        var text = "ñandú"u8.ToArray(); // C3 B1 61 6E 64 C3 BA
        Assert.Multiple(() =>
        {
            Assert.That(MessageText.ValidPrefix(text, 6).ToArray(), Is.EqualTo("ñand"u8.ToArray()), "a cap inside ú drops it whole");
            Assert.That(MessageText.ValidPrefix(text, 7).ToArray(), Is.EqualTo(text));
            Assert.That(MessageText.ValidPrefix(text, 1).ToArray(), Is.Empty, "a cap inside ñ leaves nothing");
            Assert.That(MessageText.ValidPrefix([0x41, 0xFF, 0x42], 3).ToArray(), Is.EqualTo(new byte[] { 0x41 }), "bytes that were never UTF-8 end the text");
        });
    }
}

/// <summary>The wide-body arena (13 § 6.2): size classes, in-place rewrites, moves, the budget, and nothing left behind.</summary>
[TestFixture]
public unsafe class WideBodyArenaTests
{
    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = nameof(WideBodyArenaTests) });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "WideArenaAllocator" });
    }

    [TearDown]
    public void TearDown()
    {
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    [TestCase(1, 0)]
    [TestCase(32, 0)]
    [TestCase(33, 1)]
    [TestCase(64, 1)]
    [TestCase(1026, 6)]
    [TestCase(WideBodyArena.MaxClassBytes, 13)]
    [TestCase(WideBodyArena.MaxClassBytes + 1, -1)]
    public void ABodyTakesTheSmallestClassThatHoldsIt(int length, int sizeClass) => Assert.That(WideBodyArena.ClassFor(length), Is.EqualTo(sizeClass));

    [Test]
    public void ABodyIsRewrittenInPlaceWithinItsClassAndMovedAcrossOne()
    {
        using var arena = new WideBodyArena("Arena", _registry.Runtime, _allocator, 1L << 20);
        uint handle = 0;
        Assert.That(arena.Store(ref handle, [1, 2, 3]), Is.True);
        var first = handle;
        Assert.That(arena.Store(ref handle, new byte[20]), Is.True);
        Assert.That(handle, Is.EqualTo(first), "20 bytes stay in the 32-byte class: rewritten in place");

        Assert.That(arena.Store(ref handle, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(handle, Is.Not.EqualTo(first), "100 bytes need the 128-byte class: moved");
            Assert.That(arena.Body(handle)[..100].ToArray(), Is.EqualTo(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()));
            Assert.That(arena.LiveBodies, Is.EqualTo(1), "the old slot went back when the body moved");
            Assert.That(arena.LiveBytes, Is.EqualTo(128));
        });

        arena.Free(handle);
        Assert.That((arena.LiveBodies, arena.LiveBytes), Is.EqualTo((0L, 0L)));
    }

    [Test]
    public void AnArenaPastItsBudgetRefusesAndKeepsTheBodyItHad()
    {
        // One 64 KiB slab: the 256 KiB class can never be had.
        using var arena = new WideBodyArena("Small", _registry.Runtime, _allocator, 64 * 1024);
        uint handle = 0;
        Assert.That(arena.Store(ref handle, [7, 7]), Is.True);
        var kept = handle;

        Assert.Multiple(() =>
        {
            Assert.That(arena.Store(ref handle, new byte[200 * 1024]), Is.False, "a class above the budget is refused, not thrown");
            Assert.That(handle, Is.EqualTo(kept), "and the entry keeps the body it had");
            Assert.That(arena.Body(handle)[..2].ToArray(), Is.EqualTo(new byte[] { 7, 7 }));
            Assert.That(arena.CommittedBytes, Is.EqualTo(64 * 1024));
        });
    }

    [Test]
    public void AFreeOfASlotThatIsNotLiveIsRefusedAndCounted()
    {
        using var arena = new WideBodyArena("Twice", _registry.Runtime, _allocator, 1L << 20);
        uint first = 0, second = 0;
        Assert.That(arena.Store(ref first, [1]) && arena.Store(ref second, [2]), Is.True);
        arena.Free(first);
        arena.Free(first);

        uint third = 0, fourth = 0;
        Assert.That(arena.Store(ref third, [3]) && arena.Store(ref fourth, [4]), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(arena.DoubleFrees, Is.EqualTo(1));
            Assert.That(third, Is.Not.EqualTo(fourth), "the slot was on the free list once, so two bodies never share it");
            Assert.That(new[] { third, fourth }, Has.None.EqualTo(second));
            Assert.That(arena.LiveBodies, Is.EqualTo(3));
        });
    }

    [Test]
    public void ConcurrentStoresAndFreesHandOutEachSlotOnce()
    {
        using var arena = new WideBodyArena("Concurrent", _registry.Runtime, _allocator, 64L << 20);
        const int workers = 8;
        const int rounds = 4000;
        var failures = 0;
        System.Threading.Tasks.Parallel.For(0, workers, worker =>
        {
            Span<uint> handles = stackalloc uint[16];
            var payload = new byte[256];
            for (var round = 0; round < rounds; round++)
            {
                var i = round & 15;
                var length = 1 + ((round * 37 + worker) % 200);
                payload.AsSpan(0, length).Fill((byte)worker);
                if (!arena.Store(ref handles[i], payload.AsSpan(0, length)))
                {
                    System.Threading.Interlocked.Increment(ref failures);
                    continue;
                }

                // Only this worker writes this body: anything else in it means two owners.
                if (arena.Body(handles[i])[..length].IndexOfAnyExcept((byte)worker) >= 0)
                {
                    System.Threading.Interlocked.Increment(ref failures);
                }
            }

            arena.Free(handles);
        });

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Zero);
            Assert.That(arena.DoubleFrees, Is.Zero);
            Assert.That((arena.LiveBodies, arena.LiveBytes), Is.EqualTo((0L, 0L)));
        });
    }

    [Test]
    public void DisposingFreesEverySlab()
    {
        var arena = new WideBodyArena("Disposed", _registry.Runtime, _allocator, 1L << 22);
        for (var size = 1; size <= 4096; size *= 2)
        {
            uint handle = 0;
            Assert.That(arena.Store(ref handle, new byte[size]), Is.True);
        }

        Assert.That(_allocator.PinnedLiveBlocks, Is.GreaterThan(0));
        arena.Dispose();
        Assert.That(_allocator.PinnedLiveBlocks, Is.Zero);
    }
}

/// <summary>A parked entry carries its owner entry (found writing 13 § 6.2): the drain used to copy it from past the parked bytes.</summary>
[TestFixture]
public unsafe class ParkedOwnerEntryTests
{
    [Test]
    public void AParkedEntryLandsWithItsOwnOwnerBytes()
    {
        using var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = nameof(ParkedOwnerEntryTests) });
        using var allocator = new MemoryAllocator(registry, new MemoryAllocatorOptions { Name = "ParkAllocator" });
        using var netIds = new NetIdAllocator("NetIds", registry.Runtime);
        var layout = new ReplicationBlockLayout(21, ownerEntrySize: 16);
        using var state = new ArchetypeReplicationState("Owner", registry.Runtime, allocator, layout, new SubscriptionsOptions(), netIds);

        Assert.That(state.TryAttachBlock(1, out var source), Is.True);
        var bytes = (byte*)source;
        ((ReplicationHotEntry*)(bytes + layout.HotOffset + (3 * layout.HotStride)))->NetId = 7;
        var owner = new Span<byte>(bytes + layout.OwnerOffset + (3 * layout.OwnerEntrySize), 16);
        for (var i = 0; i < owner.Length; i++)
        {
            owner[i] = (byte)(0xA0 + i);
        }

        // Two parked entries, so the first one's owner bytes would have been read from the second one's hot entry.
        ((ReplicationHotEntry*)(bytes + layout.HotOffset + (4 * layout.HotStride)))->NetId = 8;
        Assert.That(state.MigrateEntry(1, 3, 2, 5, 0), Is.EqualTo(ReplicationMigrationOutcome.Parked));
        Assert.That(state.MigrateEntry(1, 4, 2, 6, 0), Is.EqualTo(ReplicationMigrationOutcome.Parked));

        Assert.That(state.TryAttachBlock(2, out var destination), Is.True);
        Assert.That(state.DrainParkedEntries(), Is.EqualTo(2));
        var landed = new ReadOnlySpan<byte>((byte*)destination + layout.OwnerOffset + (5 * layout.OwnerEntrySize), 16).ToArray();
        Assert.That(landed, Is.EqualTo(Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray()));
    }
}

/// <summary>
/// Wide groups end to end (13 § 6, E-4, E-9): text in a public group, on enter and in the owner section reaches the replica exactly, a cap cuts at a code
/// point and is counted, a changed text is a state record, and the arena holds nothing an entry does not name.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class WideGroupTests : TestBase<WideGroupTests>
{
    private const string Profile = "world";

    internal static DatabaseEngine Engine(IServiceProvider services)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjLabel>();
        dbe.RegisterComponentFromAccessor<ProjTag>();
        dbe.RegisterComponentFromAccessor<ProjBio>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new System.Numerics.Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
            worldMax: new System.Numerics.Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM),
            cellSize: 256f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    internal static void Declare(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjLabelled>(a => a
            .Motion(ProjLabelled.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .OnEnter(ProjLabelled.Tag, x => x.Tag, Codec.Exact, name: "tag")
            .Field(ProjLabelled.Label, x => x.Name, Codec.Exact, name: "name", group: "label")
            .Field(ProjLabelled.Label, x => x.Name, Codec.Str(6), name: "brief", group: "brief")
            .Field(ProjLabelled.Bio, x => x.Bio, Codec.Str(63), name: "bio64", group: "bio")
            .Owner(o => o
                .Field(ProjLabelled.Bio, x => x.Bio, Codec.Exact, name: "bio")
                .Field(ProjLabelled.Bio, x => x.Level, Codec.Exact, name: "level")));
        subs.Profile(Profile, p => p.World().Of<ProjLabelled>());
    }

    private static ProjBounds At(float x) => new() { Bounds = new AABB2F { MinX = x - 0.5f, MinY = -0.5f, MaxX = x + 0.5f, MaxY = 0.5f }, Speed = 1f };

    internal static EntityId Spawn(DatabaseEngine dbe, float x, string name, string tag, string bio, int level)
    {
        using var tx = dbe.CreateQuickTransaction();
        var bounds = At(x);
        var label = new ProjLabel { Name = name };
        var tagged = new ProjTag { Tag = tag };
        var biography = new ProjBio { Bio = bio, Level = level };
        var id = tx.Spawn<ProjLabelled>(ProjLabelled.Bounds.Set(in bounds), ProjLabelled.Label.Set(in label), ProjLabelled.Tag.Set(in tagged),
            ProjLabelled.Bio.Set(in biography));
        tx.Commit();
        return id;
    }

    private (FrameHarness Harness, SessionId Session, int Archetype) Start(out EntityId[] ids)
    {
        var dbe = Engine(ServiceProvider);
        ids =
        [
            Spawn(dbe, 10f, "ñandú", "knight", "ñandú", 3),
            Spawn(dbe, 20f, "Sir Galahad", "squire", new string('é', 600), 7),
        ];

        var harness = FrameHarness.Create(dbe, Declare, nameof(WideGroupTests), replicationCellM: ProjectionTestSchema.ReplicationCellFor(0));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, Profile)[0];
        Assert.That(harness.Sessions.SetControlled(session, ids[1]), Is.True);
        harness.RunTick(1);
        Assert.That(harness.Deliver(session), Is.EqualTo(1));
        return (harness, session, harness.CatalogPlan.ArchetypeByName(nameof(ProjLabelled)).Idx);
    }

    [Test]
    public void TextReachesTheReplicaFromEverySectionAndACapCutsAtACodePoint()
    {
        var (harness, session, archetype) = Start(out var ids);
        using var _ = harness;
        var replica = harness.Replica(session);
        var byName = replica.NetIds(archetype).ToDictionary(n => replica.Text(archetype, n, "name"));
        var plan = harness.Subscriptions.Plans[harness.PlanIndex(nameof(ProjLabelled))];
        var state = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjLabelled))];
        var brief = Array.FindIndex(plan.Fields, f => f.Name == "brief");
        var bioRow = plan.Fields.Length + Array.FindIndex(plan.OwnerFields, f => f.Name == "bio");

        Assert.Multiple(() =>
        {
            var a = byName["ñandú"];
            var b = byName["Sir Galahad"];
            Assert.That(replica.Text(archetype, a, "tag"), Is.EqualTo("knight"), "text sent once, on enter: a wide onEnter section");
            Assert.That(replica.Text(archetype, a, "brief"), Is.EqualTo("ñand"), "str(6) cuts inside ú and drops it whole");
            Assert.That(replica.Text(archetype, b, "brief"), Is.EqualTo("Sir Ga"));
            Assert.That(replica.Text(archetype, a, "bio64"), Is.EqualTo("ñandú"));
            Assert.That(replica.Text(archetype, b, "bio64"), Is.EqualTo(new string('é', 31)),
                "String64's setter kept 63 bytes, the 32nd é cut in two; the invalid half is dropped, never sent");
            Assert.That(state.ClampsOf(brief), Is.EqualTo(2), "both briefs were cut, and each cut is counted");
            Assert.That(state.ClampsOf(bioRow), Is.EqualTo(1), "the stored half character is a cut too");
            Assert.That(replica.Store.Anomalies, Is.Zero);

            // The owner section: text in SELF, to the session controlling the entity alone.
            var self = replica.Store.Self;
            var bio = Array.Find(self.Archetype.OwnerFields, f => f.Name == "bio");
            Assert.That(self.Texts[bio.Ordinal], Is.EqualTo(new string('é', 31)));

            // E-4: a cap equal to the capacity is the same section, the same worst case and the same arena class as the exact codec.
            var label = plan.Groups.First(g => g.Name == "label").Section;
            var bio64 = plan.Groups.First(g => g.Name == "bio").Section;
            Assert.That((label.Wide, label.StoredBytes, label.MaxBodyBytes), Is.EqualTo((bio64.Wide, bio64.StoredBytes, bio64.MaxBodyBytes)));
            Assert.That(WideBodyArena.ClassFor(label.MaxBodyBytes), Is.EqualTo(WideBodyArena.ClassFor(bio64.MaxBodyBytes)));
            Assert.That(state.WideBodies.LiveBodies, Is.EqualTo(2 * 5), "two entities × (tag, label, brief, bio, owner)");
        });
    }

    [Test]
    public void AChangedTextIsAStateRecordAndAnUnchangedOneSendsNothing()
    {
        var (harness, session, archetype) = Start(out var ids);
        using var _ = harness;
        var replica = harness.Replica(session);

        harness.RunTick(2);
        harness.Deliver(session);
        var quiet = replica.Store.Archetypes[archetype];
        var netId = harness.NetIdOf(ids[0]);

        // Written through the span and pushed, as an explicit profile's system does (ADR-067).
        using (var tx = harness.Engine.CreateQuickTransaction())
        {
            var accessor = tx.For<ProjLabelled>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009
                var labels = cluster.GetSpan(ProjLabelled.Label);
#pragma warning restore TYPHON009
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (cluster.GetEntityId(slot) == ids[0])
                    {
                        labels[slot].Name = "Lancelot du Lac, a much longer name";
                        harness.Subscriptions.Commands.Replicate(in cluster, slot);
                    }
                }
            }

            tx.Commit();
        }

        harness.RunTick(3);
        Assert.That(harness.Deliver(session), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(replica.Text(archetype, netId, "name"), Is.EqualTo("Lancelot du Lac, a much longer name"), "the body grew a class and moved");
            Assert.That(replica.Text(archetype, netId, "brief"), Is.EqualTo("Lancel"));
            Assert.That(replica.Text(archetype, netId, "tag"), Is.EqualTo("knight"), "onEnter is not resent");
            Assert.That(quiet, Is.SameAs(replica.Store.Archetypes[archetype]));
        });
    }

    [Test]
    public void TheArenaHoldsNothingNoEntryNamesAfterChurn()
    {
        var (harness, session, _) = Start(out var ids);
        using var _h = harness;
        var state = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjLabelled))];
        var tick = 2L;
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var spawned = new List<EntityId>();
            for (var i = 0; i < 8; i++)
            {
                spawned.Add(Spawn(harness.Engine, 30f + (i * 2f), $"wave {cycle}-{i}", "mob", new string('x', 40 + (cycle * 30)), i));
            }

            harness.RunTick(tick++);
            harness.Deliver(session);
            using (var tx = harness.Engine.CreateQuickTransaction())
            {
                foreach (var id in spawned)
                {
                    tx.Destroy(id);
                }

                tx.Commit();
            }

            harness.RunTick(tick++);
            harness.Deliver(session);
        }

        Assert.Multiple(() =>
        {
            Assert.That(state.WideBodies.LiveBodies, Is.EqualTo(2 * 5), "only the two survivors' bodies are live");
            Assert.That(state.WideDeferrals, Is.Zero);
            Assert.That(harness.Replica(session).NetIds(harness.CatalogPlan.ArchetypeByName(nameof(ProjLabelled)).Idx), Has.Length.EqualTo(2));
        });
    }

    [Test]
    public void AnEntityCarriesItsTextAcrossAClusterChange()
    {
        var (harness, session, archetype) = Start(out var ids);
        using var _ = harness;
        var state = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjLabelled))];
        var before = harness.NetIdOf(ids[0]);

        using (var tx = harness.Engine.CreateQuickTransaction())
        {
            var accessor = tx.For<ProjLabelled>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var occupancy = cluster.OccupancyBits;
                    while (occupancy != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                        occupancy &= occupancy - 1;
                        if (cluster.GetEntityId(slot) == ids[0])
                        {
                            var far = new ProjBounds { Bounds = new AABB2F { MinX = 4000, MinY = 4000, MaxX = 4001, MaxY = 4001 }, Speed = 1f };
                            cluster.WriteSpatial(ProjLabelled.Bounds, slot, far);
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }

            tx.Commit();
        }

        // The harness runs the fence at each tick's start (RunFence), and the fence is what migrates.
        for (var tick = 2L; tick <= 4; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var replica = harness.Replica(session);
        Assert.Multiple(() =>
        {
            Assert.That(harness.NetIdOf(ids[0]), Is.EqualTo(before), "the entry was carried, not re-created");
            Assert.That(replica.Text(archetype, before, "name"), Is.EqualTo("ñandú"));
            Assert.That(state.WideBodies.LiveBodies, Is.EqualTo(2 * 5), "the bodies moved with their references: none freed, none leaked");
            Assert.That(state.EntriesMigrated + state.EntriesParked, Is.GreaterThan(0), "the entity changed cluster, so this measured something");
        });
    }

    [Test]
    public void AScalarArchetypeKeepsItsLayout()
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), ProjectionTestSchema.DeclareCreature, "Scalar");
        var plan = harness.Subscriptions.Plans[harness.PlanIndex(nameof(ProjCreature))];
        Assert.Multiple(() =>
        {
            Assert.That(plan.HasWideSections, Is.False);
            Assert.That(harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(ProjCreature))].WideBodies, Is.Null, "no arena for no text");
            Assert.That(plan.BlockLayout.PackedStateBytes, Is.EqualTo(plan.MaxStateBodyBytes), "E-9: every group stored inline, as before");
            Assert.That(plan.Groups.Select(g => g.Section.StoredOffset),
                Is.EqualTo(plan.Groups.Select((g, i) => plan.Groups.Take(i).Sum(p => p.Section.MaxBodyBytes))));
        });
    }
}

/// <summary>
/// The arena out of budget (13 § 6.2): an entity already described keeps the body it had and counts a deferral; a new one is not described at all — its
/// initialization is taken back whole, identity and bodies, and it is tried again next tick.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed unsafe class WideBodyDeferralTests : TestBase<WideBodyDeferralTests>
{
    [Test]
    public void AnArenaOutOfBudgetKeepsOldBodiesAndTakesANewEntityBack()
    {
        var engine = WideGroupTests.Engine(ServiceProvider);
        var subs = new SubscriptionsRegistry();
        WideGroupTests.Declare(subs);
        var plan = ProjectionCompiler.Compile(subs, engine, ProjectionTestSchema.TickPeriodSeconds, 1).Single(p => p.Name == nameof(ProjLabelled));
        using var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = nameof(WideBodyDeferralTests) });
        using var netIds = new NetIdAllocator("NetIds", registry.Runtime);
        using var state = new ArchetypeReplicationState("Labelled", registry.Runtime, engine.MemoryAllocator, plan.BlockLayout,
            new SubscriptionsOptions { StatePoolBudgetBytes = 16L * 1024 * 1024 }, netIds);

        // One 64 KiB slab: every body of a short text is a 32-byte one, and the first body that needs another class finds the budget spent.
        state.AttachWideSections(plan, engine.MemoryAllocator, new SubscriptionsOptions { StatePoolBudgetBytes = 64 * 1024 });
        var clusterState = engine._archetypeStates[plan.ArchetypeCatalogId].ClusterState;

        var first = WideGroupTests.Spawn(engine, 10f, "ñandú", "knight", "short", 1);
        Project(engine, plan, state, clusterState, 1);
        var bodies = state.WideBodies.LiveBodies;
        Assert.That(bodies, Is.EqualTo(5), "tag, label, brief, bio, owner");

        // A name that needs the 64-byte class: refused, the stored one kept.
        Rename(engine, first, new string('n', 40));
        Project(engine, plan, state, clusterState, 2);
        Assert.Multiple(() =>
        {
            Assert.That(state.WideDeferrals, Is.EqualTo(1));
            Assert.That(state.WideBodies.LiveBodies, Is.EqualTo(bodies), "nothing allocated, nothing lost");
        });

        // A new entity whose name needs it: not described at all.
        var second = WideGroupTests.Spawn(engine, 12f, new string('m', 40), "squire", "short", 2);
        Project(engine, plan, state, clusterState, 3);
        Assert.Multiple(() =>
        {
            Assert.That(NetIdOf(engine, plan, state, clusterState, second), Is.EqualTo(NetIdAllocator.NoNetId), "its initialization was taken back");
            Assert.That(NetIdOf(engine, plan, state, clusterState, first), Is.Not.EqualTo(NetIdAllocator.NoNetId));
            Assert.That(state.WideBodies.LiveBodies, Is.EqualTo(bodies), "the bodies it did get were freed with it");
            Assert.That(state.NetIdLeases.PendingReleases, Is.Zero, "no identity was taken, so none is given back to drain the lease");
        });
    }

    private static void Project(DatabaseEngine engine, CompiledProjectionPlan plan, ArchetypeReplicationState state, ArchetypeClusterState clusterState,
        uint tick)
    {
        using var guard = EpochGuard.Enter(engine.EpochManager);
        using var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        var ids = clusterState.ReadActiveClusterList(out var count);
        for (var i = 0; i < count; i++)
        {
            if (ids[i] >= 0 && *(ulong*)accessor.GetChunkAddress(ids[i]) != 0 && !state.Directory.TryGetBlock(ids[i], out _))
            {
                state.TryAttachBlock(ids[i], out _);
            }
        }

        state.WatchedBlocks.ClearMasks();
        state.BeginWatchedBlocks(tick);
        for (var i = 0; i < count; i++)
        {
            if (ids[i] < 0 || !state.Directory.TryGetBlock(ids[i], out var block))
            {
                continue;
            }

            var occupancy = *(ulong*)accessor.GetChunkAddress(ids[i]);
            while (occupancy != 0)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                state.WatchedBlocks.Mark(block, slot);
            }
        }

        state.BeginProjectTick(workers: 1);
        for (var i = 0; i < state.WatchedBlocks.Count; i++)
        {
            var block = state.WatchedBlocks[i];
            ProjectionPass.ProjectBlock(plan, 0, state, 0, block, accessor.GetChunkAddress(block->ChunkId), null, tick);
        }
    }

    private static uint NetIdOf(DatabaseEngine engine, CompiledProjectionPlan plan, ArchetypeReplicationState state, ArchetypeClusterState clusterState,
        EntityId entity)
    {
        using var guard = EpochGuard.Enter(engine.EpochManager);
        using var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        var ids = clusterState.ReadActiveClusterList(out var count);
        var layout = plan.BlockLayout;
        for (var i = 0; i < count; i++)
        {
            if (ids[i] < 0 || !state.Directory.TryGetBlock(ids[i], out var block))
            {
                continue;
            }

            for (var slot = 0; slot < layout.SlotCount; slot++)
            {
                var hot = (ReplicationHotEntry*)((byte*)block + layout.HotOffset + (slot * layout.HotStride));
                var stored = *(long*)(accessor.GetChunkAddress(ids[i]) + plan.ClusterLayout.EntityIdsOffset + (slot * 8));
                if (stored == (long)entity.RawValue)
                {
                    return hot->Entity == entity ? hot->NetId : NetIdAllocator.NoNetId;
                }
            }
        }

        return NetIdAllocator.NoNetId;
    }

    private static void Rename(DatabaseEngine engine, EntityId id, string name)
    {
        using var tx = engine.CreateQuickTransaction();
        tx.OpenMut(id).Write(ProjLabelled.Label).Name = name;
        tx.Commit();
    }
}

/// <summary>
/// Every site that ends or moves an entry, against a synthetic wide plan (13 § 6.2, 06 § 6): a body ends exactly once, moves with its reference, and
/// nothing is freed twice.
/// </summary>
[TestFixture]
public unsafe class WideBodyLifetimeTests
{
    private const int Slots = 21;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private NetIdAllocator _netIds;
    private ArchetypeReplicationState _state;
    private ReplicationBlockLayout _layout;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = nameof(WideBodyLifetimeTests) });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "WideLifetime" });
        _netIds = new NetIdAllocator("NetIds", _registry.Runtime);

        // One wide public group and one wide owner group: an 8-byte reference in the hot entry and one in the owner entry.
        _layout = ReplicationBlockLayout.ForArchetype(Slots, segmentBytes: 0, packedStateBytes: 8, prevPositionBytes: 0, runStartBytes: 0, ownerEntrySize: 8);
        var wide = new CompiledSection { FirstField = 0, FieldCount = 1, MaxBodyBytes = 68, Wide = true, StoredOffset = 0 };
        var plan = new CompiledProjectionPlan
        {
            Name = "Synthetic", HasWideSections = true, OnEnter = default,
            Groups = [new CompiledGroup { Name = "g", Bit = 0, TickSlot = 0, Section = wide }],
            OwnerGroups = [new CompiledGroup { Name = "o", Bit = 0, TickSlot = -1, Section = wide }],
        };
        _state = new ArchetypeReplicationState("Synthetic", _registry.Runtime, _allocator, _layout, new SubscriptionsOptions(), _netIds);
        _state.AttachWideSections(plan, _allocator, new SubscriptionsOptions());
    }

    [TearDown]
    public void TearDown()
    {
        _state?.Dispose();
        _netIds?.Dispose();
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private byte* Attach(int chunk)
    {
        Assert.That(_state.TryAttachBlock(chunk, out var block), Is.True);
        return (byte*)block;
    }

    private byte* HotRef(byte* block, int slot) => block + _layout.HotOffset + (slot * _layout.HotStride) + _layout.PackedStateOffsetInHotEntry;

    private byte* OwnerRef(byte* block, int slot) => block + _layout.OwnerOffset + (slot * _layout.OwnerEntrySize);

    // A described entry: a netId and two bodies.
    private (uint Hot, uint Owner) Describe(byte* block, int slot, uint netId)
    {
        ((ReplicationHotEntry*)(block + _layout.HotOffset + (slot * _layout.HotStride)))->NetId = netId;
        uint hot = 0, owner = 0;
        Assert.That(_state.WideBodies.Store(ref hot, [1, 2, 3]) && _state.WideBodies.Store(ref owner, [4, 5]), Is.True);
        *(uint*)HotRef(block, slot) = hot;
        *(uint*)(HotRef(block, slot) + 4) = 3;
        *(uint*)OwnerRef(block, slot) = owner;
        *(uint*)(OwnerRef(block, slot) + 4) = 2;
        return (hot, owner);
    }

    [Test]
    public void AMigrationOverAnEntryFreesItsBodiesAndMovesTheArrivingOnes()
    {
        var source = Attach(1);
        var destination = Attach(2);
        var arriving = Describe(source, 3, 7);
        var overwritten = Describe(destination, 5, 8);

        Assert.That(_state.MigrateEntry(1, 3, 2, 5, 0), Is.EqualTo(ReplicationMigrationOutcome.Carried));
        Assert.Multiple(() =>
        {
            Assert.That(*(uint*)HotRef(destination, 5), Is.EqualTo(arriving.Hot), "moved with its reference");
            Assert.That(*(uint*)OwnerRef(destination, 5), Is.EqualTo(arriving.Owner));
            Assert.That(*(uint*)HotRef(source, 3), Is.Zero, "the slot it left names nothing");
            Assert.That(_state.WideBodies.IsLive(overwritten.Hot) || _state.WideBodies.IsLive(overwritten.Owner), Is.False, "the overwritten entry ended");
            Assert.That(_state.WideBodies.LiveBodies, Is.EqualTo(2));
            Assert.That(_state.WideBodies.DoubleFrees, Is.Zero);
        });
    }

    [Test]
    public void AParkedEntryKeepsItsBodiesAndADroppedOneFreesThem()
    {
        var source = Attach(1);
        Describe(source, 3, 7);
        Assert.That(_state.MigrateEntry(1, 3, 9, 4, 0), Is.EqualTo(ReplicationMigrationOutcome.Parked));
        Assert.That(_state.WideBodies.LiveBodies, Is.EqualTo(2), "parked: moved into the list, not freed");

        // Chunk 9 never gets a block: the parked entry is dropped, and its bodies with it.
        Assert.That(_state.DrainParkedEntries(), Is.Zero);
        Assert.Multiple(() =>
        {
            Assert.That(_state.WideBodies.LiveBodies, Is.Zero);
            Assert.That(_state.ParkedDropped, Is.EqualTo(1));
            Assert.That(_state.WideBodies.DoubleFrees, Is.Zero);
        });
    }

    [Test]
    public void TheDrainOverwritingAnEntryFreesItsBodies()
    {
        var source = Attach(1);
        var arriving = Describe(source, 3, 7);
        Assert.That(_state.MigrateEntry(1, 3, 9, 4, 0), Is.EqualTo(ReplicationMigrationOutcome.Parked));
        var destination = Attach(9);
        Describe(destination, 4, 8);

        Assert.That(_state.DrainParkedEntries(), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(*(uint*)HotRef(destination, 4), Is.EqualTo(arriving.Hot));
            Assert.That(*(uint*)OwnerRef(destination, 4), Is.EqualTo(arriving.Owner), "the owner reference parked with the rest");
            Assert.That(_state.WideBodies.LiveBodies, Is.EqualTo(2));
            Assert.That(_state.WideBodies.DoubleFrees, Is.Zero);
        });
    }

    [Test]
    public void ReleasingABlockFreesItsEntriesAndARecycledBlockHoldsNoneOfThem()
    {
        var block = Attach(1);
        Describe(block, 0, 7);
        Describe(block, 20, 8);
        Assert.That(_state.TryReleaseBlock(1), Is.True);
        Assert.That(_state.WideBodies.LiveBodies, Is.Zero);

        // The pool hands the same block back, zeroed: releasing it again finds no reference to free twice.
        Attach(1);
        Assert.That(_state.TryReleaseBlock(1), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_state.WideBodies.LiveBodies, Is.Zero);
            Assert.That(_state.WideBodies.DoubleFrees, Is.Zero);
        });
    }

    [Test]
    public void AFrameLeavesOutAGroupWhoseBodyIsMissing()
    {
        var block = Attach(1);
        Describe(block, 2, 7);
        *(uint*)(OwnerRef(block, 2)) = 0;
        var walk = new ArchetypeEncodePlan.SectionWalk { Wide = true, MaxBytes = 68, Offset = 0, FieldBytes = [], FixedBytes = -1 };
        var plan = new ArchetypeEncodePlan { Layout = _layout, Groups = [walk], OwnerGroups = [walk], Arena = _state.WideBodies };

        Assert.Multiple(() =>
        {
            Assert.That(plan.PresentGroups(plan.Groups, HotRef(block, 2), 1), Is.EqualTo(1), "a body that is there is kept");
            Assert.That(plan.PresentGroups(plan.OwnerGroups, OwnerRef(block, 2), 1), Is.Zero, "a missing one is left out, not thrown over");
        });
    }
}
