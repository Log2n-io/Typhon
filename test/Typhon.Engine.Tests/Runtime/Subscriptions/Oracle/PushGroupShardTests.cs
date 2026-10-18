using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// Positionless members (14 § 5, step 4.2): a shard has no position, is reached through its town alone, and has replication state only while some session
/// holds a town of it.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushGroupShardTests : TestBase<PushGroupShardTests>
{
    private const double Radius = 120d;
    private const double Reach = 80d;
    private const double Margin = 20d;

    private static SubscriptionsOptions Options(int enterBudget = 500) => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        MaxGroupsPerSession = 8,
        EnterBudgetPerFrame = enterBudget,
        PushShadow = true,
    };

    private FrameHarness Harness(DatabaseEngine dbe, string name, int enterBudget = 500)
    {
        var harness = FrameHarness.Create(dbe, s => GroupTestSchema.Declare(s, Radius, Reach, Margin, shards: true), name, Options(enterBudget));
        harness.RunFence = true;
        harness.DrainNetIds = true;
        return harness;
    }

    private static void Town(DatabaseEngine dbe, int key, float x, float y, float half)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, half) }));
        tx.Commit();
    }

    private static List<EntityId> Shards(DatabaseEngine dbe, int key, int count, int template = 1)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids.Add(tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)(template + i) }),
                GroupPositionless.Key.Set(new GroupKey { Town = key })));
        }

        tx.Commit();
        return ids;
    }

    private static void SetKey(DatabaseEngine dbe, EntityId shard, int key)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(shard).Set(GroupPositionless.Key, new GroupKey { Town = key });
        tx.Commit();
    }

    private static int ShardPlan(FrameHarness harness) => harness.PlanIndex(nameof(GroupPositionless));

    private static int ShardBlocks(FrameHarness harness) => harness.Subscriptions.ReplicationStates[ShardPlan(harness)].Directory.Count;

    /// <summary>One tick and its delivery: the frame's completions of <paramref name="key"/> and its shard enters and leaves, or zeros with no frame.</summary>
    private static (int Completed, int Released, int Entered, int Left) Tick(FrameHarness harness, SessionId session, ref long tick, long key)
    {
        tick++;
        harness.RunTick(tick);
        if (harness.Deliver(session) == 0)
        {
            return default;
        }

        var store = harness.Replica(session).Store;
        var shards = store.Archetypes[ShardPlan(harness)];
        return (store.Groups.Count(g => g.Kind == SourceStatus.GroupComplete && g.Key == key),
            store.Groups.Count(g => g.Kind == SourceStatus.GroupReleased && g.Key == key), shards.EnteredCount, shards.LeftCount);
    }

    /// <summary>
    /// P5′: no session holds the town, and its shards have no block; one meets it, and they are attached, identified, entered and the town completed with
    /// them; the session leaves, the town is released with them, and once no key needs their block for long enough it goes back, identities and all.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-13")]
    public void AShardHasStateOnlyWhileATownOfItIsHeld()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        var shards = Shards(dbe, 7, 10);
        using var harness = Harness(dbe, nameof(AShardHasStateOnlyWhileATownOfItIsHeld));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(3000d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 3; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.That(ShardBlocks(harness), Is.Zero, "no town of theirs is held: no state");

        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var completed = 0;
        var entered = 0;
        for (var t = 0; t < 4; t++)
        {
            var f = Tick(harness, session, ref tick, 7);
            completed += f.Completed;
            entered += f.Entered;
            if (f.Completed > 0)
            {
                Assert.That(harness.Replica(session).NetIds(ShardPlan(harness)), Has.Length.EqualTo(10), "the town completes with every shard in the store");
            }
        }

        var store = harness.Replica(session).Store;
        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(entered, Is.EqualTo(10));
            Assert.That(store.IsGroupComplete(0, 7), Is.True);
            Assert.That(shards.All(s => harness.NetIdOf(s) != 0), Is.True, "every shard has an identity");
            Assert.That(ShardBlocks(harness), Is.GreaterThan(0));
        });

        // 3 km away: past every cell of the window, so the frame is a RESET — the town and its shards go with the client's store, not by records.
        harness.Sessions.SetViewpoint(session, new Vector3D(3000d, 0d, 0d));
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).Store.IsGroupComplete(0, 7), Is.False);
            Assert.That(harness.Replica(session).NetIds(ShardPlan(harness)), Is.Empty);
        });

        // Unneeded for LazyIdleTicks blocks steps, then swept: the blocks go back, and with them the identities.
        for (var t = 0; t < (int)Engine.Internals.PushHub.LazyIdleTicks + 16; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(ShardBlocks(harness), Is.Zero, "released once idle");
            Assert.That(harness.Subscriptions.Hub.LazyReleased, Is.GreaterThan(0));
            Assert.That(shards.All(s => harness.NetIdOf(s) == 0), Is.True, "no identity outlives its block");
        });

        // And met again: filled, identified anew, completed once more.
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        completed = 0;
        for (var t = 0; t < 4; t++)
        {
            completed += Tick(harness, session, ref tick, 7).Completed;
        }

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(harness.Replica(session).NetIds(ShardPlan(harness)), Has.Length.EqualTo(10));
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// A shard that moves into a held town from one nobody holds — its cluster with no block — is attached and enters; one spawned into it enters; one that
    /// moves to a town nobody holds leaves.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AShardJoiningAHeldTownEntersAndOneLeavingItLeaves()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Town(dbe, 9, 3000f, 0f, 60f);
        var held = Shards(dbe, 7, 3);
        var far = Shards(dbe, 9, 2, template: 50);
        using var harness = Harness(dbe, nameof(AShardJoiningAHeldTownEntersAndOneLeavingItLeaves));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.That(harness.Replica(session).NetIds(ShardPlan(harness)), Has.Length.EqualTo(3));

        SetKey(dbe, far[0], 7);
        var spawned = Shards(dbe, 7, 1, template: 90)[0];
        SetKey(dbe, held[0], 9);
        var entered = 0;
        var left = 0;
        for (var t = 0; t < 4; t++)
        {
            var f = Tick(harness, session, ref tick, 7);
            entered += f.Entered;
            left += f.Left;
        }

        var replica = harness.Replica(session);
        Assert.Multiple(() =>
        {
            Assert.That(entered, Is.EqualTo(2), "the shard from town 9 and the new one");
            Assert.That(left, Is.EqualTo(1), "the shard moved to town 9");
            Assert.That(replica.Store.TryLocate(harness.NetIdOf(far[0]), out _, out _), Is.True);
            Assert.That(replica.Store.TryLocate(harness.NetIdOf(spawned), out _, out _), Is.True);
            Assert.That(replica.NetIds(ShardPlan(harness)), Has.Length.EqualTo(4));
            Assert.That(replica.Value(ShardPlan(harness), harness.NetIdOf(far[0]), "kind"), Is.EqualTo(50), "its own state");
            Assert.That(replica.Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// An attached block describes every entity of its cluster (P5′): a shard spawned into it under a town nobody holds gets an identity like the rest, so
    /// it is ready the moment that town is held.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AShardSpawnedIntoAnAttachedClusterIsDescribedWhateverItsTown()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Shards(dbe, 7, 3);
        using var harness = Harness(dbe, nameof(AShardSpawnedIntoAnAttachedClusterIsDescribedWhateverItsTown));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        var stranger = Shards(dbe, 0, 1)[0];
        var other = Shards(dbe, 11, 1)[0];
        for (var t = 0; t < 3; t++)
        {
            Tick(harness, session, ref tick, 7);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.NetIdOf(stranger), Is.Not.Zero, "keyless, in an attached cluster");
            Assert.That(harness.NetIdOf(other), Is.Not.Zero, "of a town nobody holds, in an attached cluster");
            Assert.That(harness.Replica(session).NetIds(ShardPlan(harness)), Has.Length.EqualTo(3), "and neither is sent");
        });
    }

    /// <summary>
    /// SUB-31 for a lazy target: an anchor the sphere holds names a shard whose town nobody holds — no block, so its reference reads 0 and the anchor is not
    /// pushed again for it every tick. The shard joins a held town, its block attaches, and the anchor is pushed again and names it.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-31")]
    [VerifiesRule("SUB-33")]
    public void AReferenceToAShardResolvesOnceTheShardHasABlock()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Town(dbe, 9, 3000f, 0f, 60f);
        var shard = Shards(dbe, 9, 1)[0];
        EntityId anchor;
        using (var tx = dbe.CreateQuickTransaction())
        {
            anchor = tx.Spawn<GroupAnchor>(GroupAnchor.Bounds.Set(GroupTestSchema.At(10f, 0f)),
                GroupAnchor.Link.Set(new GroupShardLink { Shard = new EntityLink<GroupPositionless>(shard) }));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, s =>
        {
            s.Archetype<GroupAnchor>(a => a.Position(GroupAnchor.Bounds).Field(GroupAnchor.Link, l => l.Shard, Codec.Exact, name: "shard", group: "link"));
            s.Archetype<GroupPositionless>(a => a.Field(GroupPositionless.Ai, x => x.Template, Codec.U8, name: "kind"));
            s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
                .Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
            s.Profile("p", p => p.Sphere(Radius).Of<GroupAnchor>().Groups("town", Reach));
        }, nameof(AReferenceToAShardResolvesOnceTheShardHasABlock), Options());
        harness.RunFence = true;
        harness.DrainNetIds = true;
        var session = harness.OpenSessions(1, "p")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var anchorPlan = harness.PlanIndex(nameof(GroupAnchor));
        var replica = harness.Replica(session);
        var tick = 0L;
        void Run(int ticks)
        {
            for (var t = 0; t < ticks; t++)
            {
                harness.RunTick(++tick);
                harness.Deliver(session);
            }
        }

        Run(4);
        var pushedBefore = harness.Subscriptions.Hub.SlotsPushed;
        Run(4);
        Assert.Multiple(() =>
        {
            Assert.That(replica.Value(anchorPlan, harness.NetIdOf(anchor), "shard"), Is.Zero, "the shard has no block: 0");
            Assert.That(harness.NetIdOf(shard), Is.Zero);
            Assert.That(harness.Subscriptions.Hub.SlotsPushed, Is.EqualTo(pushedBefore), "and the anchor is not pushed again every tick for it");
        });

        SetKey(dbe, shard, 7);
        Run(4);
        Assert.Multiple(() =>
        {
            Assert.That(harness.NetIdOf(shard), Is.Not.Zero, "its town is held: a block, an identity");
            Assert.That(replica.Value(anchorPlan, harness.NetIdOf(anchor), "shard"), Is.EqualTo(harness.NetIdOf(shard)), "the anchor names it now");
            Assert.That(harness.Subscriptions.Hub.ReferrersWoken, Is.GreaterThan(0));
            Assert.That(replica.Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// #1389's scenario (14 § 2): a group of two jointed structures, A and B, their composition shards and the joint record. The session's sphere reaches
    /// neither structure; the group's box enters its reach, and all of it is delivered under the enter budget, a RESET in the middle of the fill filling it
    /// again and completing it once. The joint breaks — the group splits, B and its shards in a group of their own the session does not meet — and they
    /// leave; a new joint merges them back, and they enter again, the group held complete throughout.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    [VerifiesRule("SUB-16")]
    public void JointedStructuresAndTheirShardsFollowTheirGroupThroughASplitAndAMerge()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        EntityId group1, a, b, joint;
        var shardsOfA = new List<EntityId>();
        var shardsOfB = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            group1 = tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = 1, Extent = new AABB2F { MinX = 140, MinY = -60, MaxX = 410, MaxY = 60 } }));
            a = tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(GroupTestSchema.At(150f, 0f)), GroupBuilding.Ai.Set(new ProjAi { Template = 1 }),
                GroupBuilding.Key.Set(new GroupKey { Town = 1 }));
            b = tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(GroupTestSchema.At(400f, 0f)), GroupBuilding.Ai.Set(new ProjAi { Template = 2 }),
                GroupBuilding.Key.Set(new GroupKey { Town = 1 }));
            for (var i = 0; i < 2; i++)
            {
                shardsOfA.Add(tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)(10 + i) }),
                    GroupPositionless.Key.Set(new GroupKey { Town = 1 })));
                shardsOfB.Add(tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)(20 + i) }),
                    GroupPositionless.Key.Set(new GroupKey { Town = 1 })));
            }

            joint = tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = 99 }), GroupPositionless.Key.Set(new GroupKey { Town = 1 }));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, s =>
        {
            s.Static<GroupBuilding>(x => x.Position(GroupBuilding.Bounds).Field(GroupBuilding.Ai, v => v.Template, Codec.U8, name: "kind"));
            s.Archetype<GroupPositionless>(x => x.Field(GroupPositionless.Ai, v => v.Template, Codec.U8, name: "kind"));
            s.Groups("structure", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
                .Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town))
                .Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town))
                .Leave(Margin));
            s.Profile("p", p => p.Sphere(50).Of<GroupBuilding>().Groups("structure", Reach));
        }, nameof(JointedStructuresAndTheirShardsFollowTheirGroupThroughASplitAndAMerge), Options(enterBudget: 2));
        harness.RunFence = true;
        harness.DrainNetIds = true;
        var session = harness.OpenSessions(1, "p")[0];
        var replica = harness.Replica(session);
        var buildings = harness.PlanIndex(nameof(GroupBuilding));
        var shards = harness.PlanIndex(nameof(GroupPositionless));
        var tick = 0L;
        var completions = 0;
        void Run(int ticks)
        {
            for (var t = 0; t < ticks; t++)
            {
                harness.RunTick(++tick);
                if (harness.Deliver(session) > 0)
                {
                    completions += replica.Store.Groups.Count(g => g.Kind == SourceStatus.GroupComplete && g.Key == 1);
                }
            }
        }

        int Held() => replica.NetIds(buildings).Length + replica.NetIds(shards).Length;

        // 80 m from the group's box: met; 90 m from A and 340 m from B, neither in the 50 m sphere. Two members a frame.
        harness.Sessions.SetViewpoint(session, new Vector3D(60d, 0d, 0d));
        Run(2);
        Assert.Multiple(() =>
        {
            Assert.That(Held(), Is.InRange(1, 6), "the fill is under way");
            Assert.That(replica.Store.IsGroupComplete(0, 1), Is.False);
        });

        // A jump past a cell, still in reach of the box: a RESET in the middle of the fill — filled again from nothing, completed once.
        var resets = harness.Subscriptions.Push.GroupResetsMidFill;
        completions = 0;
        harness.Sessions.SetViewpoint(session, new Vector3D(60d, 55d, 0d));
        Run(8);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.Push.GroupResetsMidFill, Is.GreaterThan(resets), "the reset met the group filling");
            Assert.That(completions, Is.EqualTo(1), "completed once after the reset");
            Assert.That(Held(), Is.EqualTo(7), "both structures, four shards and the joint");
        });

        // The joint breaks: B and its shards form a group of their own, which the session does not meet; the joint record goes.
        completions = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = 2, Extent = new AABB2F { MinX = 380, MinY = -60, MaxX = 420, MaxY = 60 } }));
            tx.OpenMut(group1).Set(GroupTown.Zone, new GroupZone { Key = 1, Extent = new AABB2F { MinX = 140, MinY = -60, MaxX = 170, MaxY = 60 } });
            tx.OpenMut(b).Set(GroupBuilding.Key, new GroupKey { Town = 2 });
            foreach (var shard in shardsOfB)
            {
                tx.OpenMut(shard).Set(GroupPositionless.Key, new GroupKey { Town = 2 });
            }

            tx.Destroy(joint);
            tx.Commit();
        }

        Run(4);
        Assert.Multiple(() =>
        {
            Assert.That(replica.Store.TryLocate(harness.NetIdOf(a), out _, out _), Is.True, "A stays");
            Assert.That(Held(), Is.EqualTo(3), "A and its two shards; B, its shards and the joint left");
            Assert.That(shardsOfA.All(x => replica.Store.TryLocate(harness.NetIdOf(x), out _, out _)), Is.True, "A's shards stay");
            Assert.That(replica.Store.IsGroupComplete(0, 1), Is.True, "A's group is held complete throughout");
            Assert.That(replica.Store.IsGroupComplete(0, 2), Is.False, "B's group is not met");
            Assert.That(completions, Is.Zero, "and not completed again");
        });

        // A new joint merges them back: one group again, its box over both.
        EntityId joint2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            joint2 = tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = 98 }), GroupPositionless.Key.Set(new GroupKey { Town = 1 }));
            tx.OpenMut(group1).Set(GroupTown.Zone, new GroupZone { Key = 1, Extent = new AABB2F { MinX = 140, MinY = -60, MaxX = 410, MaxY = 60 } });
            tx.OpenMut(b).Set(GroupBuilding.Key, new GroupKey { Town = 1 });
            foreach (var shard in shardsOfB)
            {
                tx.OpenMut(shard).Set(GroupPositionless.Key, new GroupKey { Town = 1 });
            }

            tx.Commit();
        }

        Run(4);
        Assert.Multiple(() =>
        {
            Assert.That(Held(), Is.EqualTo(7), "both structures, four shards and the new joint");
            Assert.That(replica.Store.TryLocate(harness.NetIdOf(joint2), out _, out _), Is.True);
            Assert.That(replica.Value(shards, harness.NetIdOf(joint2), "kind"), Is.EqualTo(98));
            Assert.That(completions, Is.Zero, "a member added to a complete group arrives as an enter");
            Assert.That(replica.Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>What every failure of the off-DAG scenario starts with: the mutant's evidence that its assertions, not the scaffolding, rejected.</summary>
    private const string OffDagMarker = "off-DAG move:";

    /// <summary>
    /// 14 § 5's race, staged: a town of Versioned shards fills two a frame; between one tick's projection and its gather, a commit on another thread — off
    /// the DAG, which EW-02 allows for a non-spatial Versioned component — moves the first unsent shard to a town nobody holds. The tree no longer lists it,
    /// its events still file it under the town: the fill reads the move log and sends it, and its leave follows the next tick. Without the log the cursor
    /// would pass it unsent and the leave would name an entity the client never had.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AnOffDagMoveOutOfAFillingTownIsSentThenLeaves() => OffDagMoveOutOfAFillingTown(withLog: true, nameof(AnOffDagMoveOutOfAFillingTownIsSentThenLeaves));

    /// <summary>The same scenario with the move log taken away: the fill passes the moved shard unsent, and the leave that follows must be caught.</summary>
    [Test]
    [RuleMutant("SUB-33")]
    public void WithoutTheMoveLogAnOffDagMoveIsCaught() =>
        RuleMutants.AssertDetects("SUB-33", OffDagMarker, () => OffDagMoveOutOfAFillingTown(withLog: false, nameof(WithoutTheMoveLogAnOffDagMoveIsCaught)));

    private void OffDagMoveOutOfAFillingTown(bool withLog, string name)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        var shards = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 6; i++)
            {
                shards.Add(tx.Spawn<GroupVersionedShard>(GroupVersionedShard.Ai.Set(new ProjAi { Template = (byte)(1 + i) }),
                    GroupVersionedShard.Key.Set(new GroupVersionedKey { Town = 7 })));
            }

            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, s =>
        {
            s.Archetype<GroupVersionedShard>(a => a.Field(GroupVersionedShard.Ai, x => x.Template, Codec.U8, name: "kind"));
            s.Groups("town", g => g.Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
                .Member<GroupVersionedShard>(m => m.Key(GroupVersionedShard.Key, k => k.Town)));
            s.Profile("p", p => p.Sphere(20).Groups("town", Reach));
        }, name, Options(enterBudget: 2));
        harness.RunFence = true;
        harness.DrainNetIds = true;
        var member = harness.Subscriptions.GroupSets[0].Members[0];
        Assert.That(member.Moves, Is.Not.Null, "a positionless member with a Versioned key has its archetype's move log");
        if (!withLog)
        {
            member.Moves = null;
        }

        var session = harness.OpenSessions(1, "p")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var plan = harness.PlanIndex(nameof(GroupVersionedShard));
        var replica = harness.Replica(session);
        var tick = 0L;

        // Met, then attached and identified: two shards a frame from here.
        for (var t = 0; t < 2; t++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var held = new HashSet<uint>(replica.NetIds(plan));
        var target = shards.Where(e => !held.Contains(harness.NetIdOf(e))).OrderBy(e => harness.NetIdOf(e)).First();
        Assert.Multiple(() =>
        {
            Assert.That(held, Has.Count.EqualTo(2), "the fill is under way");
            Assert.That(harness.NetIdOf(target), Is.Not.Zero, "the shard has its identity: the fill reads it next");
            Assert.That(replica.Store.IsGroupComplete(0, 7), Is.False);
        });

        var moveAt = (uint)(tick + 1);
        harness.Subscriptions.Hub.AfterIndexForTest = t =>
        {
            if (t != moveAt)
            {
                return;
            }

            // Another thread: a commit off the DAG, between the projection that read the key and the gather that fills by it.
            System.Threading.Tasks.Task.Run(() =>
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(target).Set(GroupVersionedShard.Key, new GroupVersionedKey { Town = 9 });
                tx.Commit();
            }).Wait();
        };

        for (var t = 0; t < 6; t++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var anomalies = replica.Store.Anomalies;
        var illegal = harness.Subscriptions.Push.ShadowIllegal;
        Assert.That(anomalies == 0 && illegal == 0, Is.True,
            $"{OffDagMarker} the client recorded {anomalies} anomalies and the shadow {illegal} illegal records: a shard left that was never sent");
        Assert.Multiple(() =>
        {
            Assert.That(replica.Store.IsGroupComplete(0, 7), Is.True, $"{OffDagMarker} the town completes");
            Assert.That(replica.NetIds(plan), Has.Length.EqualTo(5), $"{OffDagMarker} every shard still in town 7, and not the one that left");
            Assert.That(member.Moves?.Appended ?? 1, Is.GreaterThan(0), "the commit logged its move");
        });
    }

    /// <summary>A town of 20 shards under an enter budget of 4 fills over frames: no frame enters more than the budget, and it completes once, with all.</summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void ShardsFillUnderTheEnterBudget()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe, 7, 0f, 0f, 60f);
        Shards(dbe, 7, 20);
        using var harness = Harness(dbe, nameof(ShardsFillUnderTheEnterBudget), enterBudget: 4);
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var tick = 0L;
        var completed = 0;
        var entered = 0;
        var widest = 0;
        for (var t = 0; t < 12; t++)
        {
            var f = Tick(harness, session, ref tick, 7);
            completed += f.Completed;
            entered += f.Entered;
            widest = System.Math.Max(widest, f.Entered);
        }

        Assert.Multiple(() =>
        {
            Assert.That(widest, Is.LessThanOrEqualTo(4), "never past the budget");
            Assert.That(entered, Is.EqualTo(20));
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }
}
