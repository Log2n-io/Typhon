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

/// <summary>A link: a typed reference in a group, an untyped one in the owner section (13 § 5), and a pet only the refusal test declares.</summary>
[Component("Typhon.Test.Proj.Link", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjLink
{
    [Field]
    public EntityLink<ProjLinked> Target;

    [Field]
    public EntityId Other;

    [Field]
    public EntityLink<ProjCreature> Pet;

    [Field]
    public int Level;
}

[Archetype]
partial class ProjLinked : Archetype<ProjLinked>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjLink> Link = Register<ProjLink>();
}

/// <summary>A command naming an entity by the server's EntityId, which a client never holds.</summary>
struct ProjAimAt
{
#pragma warning disable CS0649 // never written: the declaration is refused before any command is decoded into it
    public EntityLink<ProjLinked> Target;
#pragma warning restore CS0649
}

/// <summary>The reference pairings (13 § 2.3, § 5): an entity field travels as entityRef and as nothing else; an integer is never taken for one.</summary>
[TestFixture]
public class ReferencePairingTests
{
    private static readonly CatalogCodec EntityRef = new() { Kind = CodecKind.EntityRef };

    [TestCase(typeof(EntityId))]
    [TestCase(typeof(EntityLink<ProjLinked>))]
    public void AnEntityFieldIsAReferenceByDefault(Type source)
    {
        var codec = CodecPairing.Resolve(source, Codec.Exact, "Field 'x'", out _);
        Assert.Multiple(() =>
        {
            Assert.That(codec.Catalog.Kind, Is.EqualTo(CodecKind.EntityRef));
            Assert.That(Codec.Declared(source, CodecKind.Unknown, 0, 0, 0, 0, 0).Catalog.Kind, Is.EqualTo(CodecKind.EntityRef), "a bare [Replicate]");
            Assert.That(CodecPairing.Classify(source, EntityRef, false, "Field 'x'"), Is.EqualTo(ColumnPath.EntityRef));
            Assert.That(CodecPairing.ReferenceTarget(source), Is.EqualTo(source == typeof(EntityId) ? null : typeof(ProjLinked)));
        });
    }

    [Test]
    public void BothEntityTypesAreSchemaLongs()
    {
        // Unmapped, a component's EntityId field was dropped from its schema, silently: pinned, so a rename of either type fails here.
        Assert.That(DatabaseSchemaExtensions.FromType(typeof(EntityId)).field, Is.EqualTo(FieldType.Long));
        Assert.That(DatabaseSchemaExtensions.FromType(typeof(EntityLink<ProjLinked>)).field, Is.EqualTo(FieldType.Long));
    }

    [Test]
    public void ACommandNamingAnEntityByItsEntityIdIsRefused()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new SubscriptionsRegistry().Command<ProjAimAt>(c => c.Rate(4, 8)));
        Assert.That(refused!.Message, Does.Contain("TryResolve"));
    }

    [Test]
    public void AnEntityInAnyOtherCodecAndAnIntegerAsAReferenceAreRefused()
    {
        Assert.Multiple(() =>
        {
            var u64 = Assert.Throws<InvalidOperationException>(() =>
                CodecPairing.Classify(typeof(EntityId), new CatalogCodec { Kind = CodecKind.U64 }, false, "Field 'x'"));
            Assert.That(u64!.Message, Does.Contain("entityRef"));
            Assert.Throws<InvalidOperationException>(() => CodecPairing.Classify(typeof(long), EntityRef, false, "Field 'x'"));
            Assert.That(CodecPairing.Classify(typeof(EntityId), EntityRef, false, "Field 'x'", message: true), Is.EqualTo(ColumnPath.None),
                "a message's reference is its binder's");
        });
    }
}

/// <summary>
/// References on entity fields (13 § 5, E-6, SUB-31): a reference carries its target's netId; a target identified this tick is resolved the next; a
/// destroyed target makes every referrer send 0 within one tick; and with identities reused, no frame names the wrong holder.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class ReferenceTests : TestBase<ReferenceTests>
{
    private const string Profile = "world";

    private static DatabaseEngine Engine(IServiceProvider services)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjLink>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new System.Numerics.Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
            worldMax: new System.Numerics.Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM),
            cellSize: 256f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjLinked>(a => a
            .Motion(ProjLinked.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjLinked.Link, x => x.Target, Codec.Exact, name: "target", group: "link")
            .Field(ProjLinked.Link, x => x.Level, Codec.Exact, name: "level", group: "link")
            .Owner(o => o.Field(ProjLinked.Link, x => x.Other, Codec.EntityRef, name: "other")));
        subs.Profile(Profile, p => p.World().Of<ProjLinked>());
    }

    private static ProjBounds At(float x) => new() { Bounds = new AABB2F { MinX = x - 0.5f, MinY = -0.5f, MaxX = x + 0.5f, MaxY = 0.5f }, Speed = 1f };

    private static EntityId Spawn(DatabaseEngine dbe, float x, EntityId target = default, EntityId other = default)
    {
        using var tx = dbe.CreateQuickTransaction();
        var bounds = At(x);
        var link = new ProjLink { Target = target, Other = other, Level = 1 };
        var id = tx.Spawn<ProjLinked>(ProjLinked.Bounds.Set(in bounds), ProjLinked.Link.Set(in link));
        tx.Commit();
        return id;
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

    // Written through the span and pushed, as an explicit profile's system does (ADR-067).
    private static void Retarget(FrameHarness harness, EntityId referrer, EntityId target)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjLinked>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
#pragma warning disable TYPHON009
            var links = cluster.GetSpan(ProjLinked.Link);
#pragma warning restore TYPHON009
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                if (cluster.GetEntityId(slot) == referrer)
                {
                    links[slot].Target = target;
                    harness.Subscriptions.Commands.Replicate(in cluster, slot);
                }
            }
        }

        tx.Commit();
    }

    private (FrameHarness Harness, SessionId Session, int Archetype) Start(DatabaseEngine dbe, SubscriptionsOptions options = null)
    {
        var harness = FrameHarness.Create(dbe, Declare, nameof(ReferenceTests), options, replicationCellM: ProjectionTestSchema.ReplicationCellFor(0));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, Profile)[0];
        return (harness, session, harness.CatalogPlan.ArchetypeByName(nameof(ProjLinked)).Idx);
    }

    private static uint Target(FrameHarness harness, SessionId session, int archetype, EntityId referrer) =>
        (uint)(harness.Replica(session).Value(archetype, harness.NetIdOf(referrer), "target") ?? double.NaN);

    [Test]
    public void AReferenceCarriesItsTargetsNetIdAndATargetIdentifiedThisTickResolvesTheNext()
    {
        var dbe = Engine(ServiceProvider);
        var a = Spawn(dbe, 10f);
        var (harness, session, archetype) = Start(dbe);
        using var _ = harness;
        harness.RunTick(1);
        harness.Deliver(session);

        var b = Spawn(harness.Engine, 20f, target: a);
        harness.RunTick(2);
        harness.Deliver(session);
        Assert.That(Target(harness, session, archetype, b), Is.EqualTo(harness.NetIdOf(a)), "a target identified before this tick resolves at once");

        // Both spawned in one tick: the target's identity is taken during this projection, so the reference reads 0 now — whichever entity is projected
        // first — and its referrer is pushed again by the engine, with no write of the application's.
        var c = Spawn(harness.Engine, 30f);
        var d = Spawn(harness.Engine, 40f, target: c);
        harness.RunTick(3);
        harness.Deliver(session);
        var index = harness.Subscriptions.References;
        Assert.That(Target(harness, session, archetype, d), Is.Zero);

        harness.RunTick(4);
        harness.Deliver(session);
        Assert.That(Target(harness, session, archetype, d), Is.EqualTo(harness.NetIdOf(c)));

        // The other order: the referrer spawned first sits in a lower slot of the same cluster and is projected before its target, whose entry does not
        // name it yet. The same answer.
        var e = Spawn(harness.Engine, 50f);
        var f = Spawn(harness.Engine, 60f);
        Retarget(harness, e, f);
        harness.RunTick(5);
        harness.Deliver(session);
        Assert.That(Target(harness, session, archetype, e), Is.Zero);
        harness.RunTick(6);
        harness.Deliver(session);
        Assert.That(Target(harness, session, archetype, e), Is.EqualTo(harness.NetIdOf(f)));

        // The index is merged from a projection's logs at the next blocks step.
        harness.RunTick(7);
        Assert.Multiple(() =>
        {
            Assert.That(index.CountOf(harness.NetIdOf(c), d), Is.EqualTo(1));
            Assert.That(index.CountOf(harness.NetIdOf(a), b), Is.EqualTo(1));
            Assert.That(index.CountOf(harness.NetIdOf(f), e), Is.EqualTo(1));
            Assert.That(index.PairCount, Is.EqualTo(3));
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
        });
    }

    [Test]
    public void ADestroyedTargetMakesEveryReferrerSendZeroWithinOneTick()
    {
        var dbe = Engine(ServiceProvider);
        var a = Spawn(dbe, 10f);
        var (harness, session, archetype) = Start(dbe);
        using var _ = harness;
        harness.RunTick(1);
        var b1 = Spawn(harness.Engine, 20f, target: a);
        var b2 = Spawn(harness.Engine, 30f, target: a, other: a);
        Assert.That(harness.Sessions.SetControlled(session, b2), Is.True);
        harness.RunTick(2);
        harness.RunTick(3);
        harness.Deliver(session);

        var replica = harness.Replica(session);
        var other = Array.Find(replica.Store.Self.Archetype.OwnerFields, f => f.Name == "other");
        var held = harness.NetIdOf(a);
        Assert.Multiple(() =>
        {
            Assert.That(Target(harness, session, archetype, b1), Is.EqualTo(held));
            Assert.That(Target(harness, session, archetype, b2), Is.EqualTo(held));
            Assert.That(replica.Store.Self.Numbers[other.Ordinal][0], Is.EqualTo(held), "an untyped EntityId, in the owner section");
            Assert.That(harness.Subscriptions.References.CountOf(held, b2), Is.EqualTo(2), "two of b2's fields name it");
        });

        // Neither referrer is written or pushed by the application: the target's release is what pushes them.
        Destroy(harness.Engine, a);
        harness.RunTick(4);
        harness.Deliver(session);
        harness.RunTick(5);
        harness.Deliver(session);
        Assert.Multiple(() =>
        {
            Assert.That(Target(harness, session, archetype, b1), Is.Zero);
            Assert.That(Target(harness, session, archetype, b2), Is.Zero);
            Assert.That(replica.Store.Self.Numbers[other.Ordinal][0], Is.Zero);
            Assert.That(harness.Subscriptions.References.ReferrersRepushed, Is.EqualTo(2), "b1 and b2, once each");
            Assert.That(harness.Subscriptions.References.PairCount, Is.Zero, "nothing names a released identity");
            Assert.That(harness.Subscriptions.References.DroppedDecrements, Is.Zero, "the taken referrers' held netIds were zeroed");
        });
    }

    [Test]
    public void TheIndexCountsExactlyWhatLiveEntriesNameAfterChurn()
    {
        var dbe = Engine(ServiceProvider);
        var keeper = Spawn(dbe, 10f);
        var (harness, session, _) = Start(dbe);
        using var _h = harness;
        var tick = 1L;
        harness.RunTick(tick++);
        var survivors = new List<EntityId>();
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var wave = new List<EntityId>();
            for (var i = 0; i < 6; i++)
            {
                wave.Add(Spawn(harness.Engine, 20f + i, target: keeper, other: keeper));
            }

            harness.RunTick(tick++);
            harness.RunTick(tick++);
            harness.Deliver(session);

            // One referrer of the wave moves to another, one survives, the rest die while naming the long-lived target.
            Retarget(harness, wave[0], wave[1]);
            survivors.Add(wave[5]);
            harness.RunTick(tick++);
            Destroy(harness.Engine, wave.Take(5).ToArray());
            harness.RunTick(tick++);
            harness.RunTick(tick++);
            harness.Deliver(session);
        }

        var index = harness.Subscriptions.References;
        Assert.Multiple(() =>
        {
            Assert.That(index.PairCount, Is.EqualTo(survivors.Count), "a pair per surviving referrer; the dead ones' are gone");
            Assert.That(survivors.All(s => index.CountOf(harness.NetIdOf(keeper), s) == 2), Is.True);
        });
    }

    [Test]
    public void AReferenceOnEnterOrToAnArchetypeNobodyObservesIsRefused()
    {
        var dbe = Engine(ServiceProvider);
        var onEnter = Assert.Throws<InvalidOperationException>(() => FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<ProjLinked>(a => a
                .Motion(ProjLinked.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
                .OnEnter(ProjLinked.Link, x => x.Target, Codec.Exact, name: "target"));
            subs.Profile(Profile, p => p.World().Of<ProjLinked>());
        }, "OnEnter"));
        var unobserved = Assert.Throws<InvalidOperationException>(() => FrameHarness.Create(dbe, subs =>
        {
            subs.Archetype<ProjLinked>(a => a
                .Motion(ProjLinked.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
                .Field(ProjLinked.Link, x => x.Pet, Codec.Exact, name: "pet", group: "link"));
            subs.Profile(Profile, p => p.World().Of<ProjLinked>());
        }, "Unobserved"));
        var onStatic = Assert.Throws<InvalidOperationException>(() => FrameHarness.Create(dbe, subs =>
        {
            subs.Static<ProjLinked>(a => a
                .Position(ProjLinked.Bounds)
                .OnEnter(ProjLinked.Link, x => x.Target, Codec.Exact, name: "target"));
            subs.Profile(Profile, p => p.World().Of<ProjLinked>());
        }, "Static"));
        Assert.Multiple(() =>
        {
            Assert.That(onEnter!.Message, Does.Contain("SUB-31"));
            Assert.That(onStatic!.Message, Does.Contain("static archetype"));
            Assert.That(unobserved!.Message, Does.Contain(nameof(ProjCreature)));
        });
    }

    /// <summary>
    /// SUB-31's oracle: referrers aimed at targets that churn — some at a target already dead — with the harness's one-tick quarantine, so released
    /// identities are reissued within a few ticks. In every frame, a reference names 0 or its own target's netId — never another live entity's — and one
    /// naming an identity nobody holds is fixed by the next frame.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-31")]
    public void UnderReuseNoFrameNamesAnotherHolder()
    {
        var dbe = Engine(ServiceProvider);
        var (harness, session, archetype) = Start(dbe);
        using var _ = harness;
        harness.DrainNetIds = true;
        var random = new Random(1085);
        var targets = new List<EntityId>();
        var dead = new List<EntityId>();
        var referrers = new Dictionary<EntityId, EntityId>();
        var stale = new HashSet<EntityId>();
        var tick = 1L;
        harness.RunTick(tick++);
        for (var i = 0; i < 12; i++)
        {
            targets.Add(Spawn(harness.Engine, -100f - i));
        }

        for (var i = 0; i < 12; i++)
        {
            var target = targets[random.Next(targets.Count)];
            referrers[Spawn(harness.Engine, 100f + i, target: target)] = target;
        }

        var minted = harness.Subscriptions.NetIds.IdentityFlow.Reused;
        var checkedFrames = 0;
        for (var step = 0; step < 120; step++)
        {
            // Churn: a target dies, a new one is born, a referrer is re-aimed — one time in four at a target already dead, which must read 0.
            if (step % 3 == 0)
            {
                var victim = targets[random.Next(targets.Count)];
                targets.Remove(victim);
                dead.Add(victim);
                Destroy(harness.Engine, victim);
                targets.Add(Spawn(harness.Engine, -100f - step));
            }

            if (step % 2 == 0)
            {
                var referrer = referrers.Keys.ElementAt(random.Next(referrers.Count));
                var target = dead.Count > 0 && random.Next(4) == 0 ? dead[random.Next(dead.Count)] : targets[random.Next(targets.Count)];
                referrers[referrer] = target;
                Retarget(harness, referrer, target);
            }

            harness.RunTick(tick++);
            harness.Deliver(session);

            var holders = new Dictionary<uint, EntityId>();
            foreach (var entity in targets.Concat(referrers.Keys))
            {
                var netId = harness.NetIdOf(entity);
                if (netId != 0)
                {
                    holders[netId] = entity;
                }
            }

            foreach (var (referrer, target) in referrers)
            {
                var netId = harness.NetIdOf(referrer);
                var named = (uint?)harness.Replica(session).Value(archetype, netId, "target");
                if (named is null or 0)
                {
                    stale.Remove(referrer);
                    continue;
                }

                checkedFrames++;
                if (holders.TryGetValue(named.Value, out var holder))
                {
                    Assert.That(holder, Is.EqualTo(target), $"tick {tick - 1}: a referrer names netId {named}, held by another entity");
                    stale.Remove(referrer);
                }
                else
                {
                    Assert.That(stale.Add(referrer), Is.True, $"tick {tick - 1}: a referrer named an identity nobody holds for a second frame");
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.NetIds.IdentityFlow.Reused - minted, Is.GreaterThan(0), "identities were reissued, so reuse was exercised");
            Assert.That(harness.Subscriptions.References.DroppedDecrements, Is.Zero);
            Assert.That(checkedFrames, Is.GreaterThan(500));
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
        });
    }
}
