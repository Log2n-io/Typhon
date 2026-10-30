using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.MixDisc.UniqState", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MixDiscUniqState
{
    [Index] public int Code;
    public int Pad;
}

[Archetype]
class MixDiscUniqArch : Archetype<MixDiscUniqArch>
{
    public static readonly Comp<MixDiscUniqState> State = Register<MixDiscUniqState>();
    public static readonly Comp<TIdxRuntime> Runtime = Register<TIdxRuntime>();
}

[Component("Typhon.Test.MixDisc.OtherState", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MixDiscOtherState
{
    [Index(AllowMultiple = true)] public int Group;
    public int Pad;
}

[Archetype]
class MixDiscOneMarkArch : Archetype<MixDiscOneMarkArch>
{
    public static readonly Comp<MixDiscUniqState> State = Register<MixDiscUniqState>();
    public static readonly Comp<MixDiscOtherState> Other = Register<MixDiscOtherState>();
}

/// <summary>
/// #1339 (IX-06, IX-07): one entity written in one tick by a Commit-discipline transaction and by a TickFence one. A Commit transaction moves a
/// SingleVersion key at its publish and a TickFence one at the fence, while the shadow mark that says "this entity's old keys are recorded" is per
/// entity: it must say the same thing whichever discipline set it.
/// </summary>
/// <remarks>Reuses <see cref="TIdxMixedArch"/>: a SingleVersion <see cref="TIdxState"/> (Hp, multi-value index) and a Transient <see cref="TIdxRuntime"/>
/// (Bucket, multi-value index).</remarks>
[TestFixture]
class MixedDisciplineIndexTests : TestBase<MixedDisciplineIndexTests>
{
    private const int Others = 100;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TIdxState>();
        dbe.RegisterComponentFromAccessor<TIdxRuntime>();
        dbe.RegisterComponentFromAccessor<MixDiscUniqState>();
        dbe.RegisterComponentFromAccessor<MixDiscOtherState>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary><see cref="Others"/> entities with <c>Hp = i</c>, then the subject with <c>Hp = 500</c> and <c>Bucket = 1</c>, fenced.</summary>
    private static EntityId Spawn(DatabaseEngine dbe)
    {
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Others; i++)
            {
                tx.Spawn<TIdxMixedArch>(TIdxMixedArch.State.Set(new TIdxState(i)), TIdxMixedArch.Runtime.Set(new TIdxRuntime(i % 4, 0)));
            }

            id = tx.Spawn<TIdxMixedArch>(TIdxMixedArch.State.Set(new TIdxState(500)), TIdxMixedArch.Runtime.Set(new TIdxRuntime(1, 0)));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);
        return id;
    }

    private static void SetHp(DatabaseEngine dbe, EntityId id, int hp, CommitDiscipline discipline)
    {
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, discipline);
        tx.OpenMut(id).Set(TIdxMixedArch.State, new TIdxState(hp));
        Assert.That(tx.Commit(), Is.True);
    }

    private static void SetBucket(DatabaseEngine dbe, EntityId id, int bucket, CommitDiscipline discipline)
    {
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, discipline);
        tx.OpenMut(id).Set(TIdxMixedArch.Runtime, new TIdxRuntime(bucket, 1));
        Assert.That(tx.Commit(), Is.True);
    }

    private static void AssertHpIndexed(DatabaseEngine dbe, EntityId id, int hp, int oldHp, string when)
    {
        IndexDataOracle.AssertIndexAgreesWithData<TIdxMixedArch>(dbe, when);
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<TIdxMixedArch>().WhereField<TIdxState>(s => s.Hp == hp).Execute(), Is.EquivalentTo(new[] { id }), $"{when}: new Hp");
        Assert.That(tx.Query<TIdxMixedArch>().WhereField<TIdxState>(s => s.Hp == oldHp).Execute(), Is.Empty, $"{when}: old Hp");
    }

    /// <summary>The issue's sequence: the Commit transaction's capture records the Transient keys only and marks the entity; the TickFence transaction's
    /// SingleVersion move then finds the mark and records nothing.</summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void CommitWritesTransient_ThenTickFenceMovesSingleVersion_TheFenceMovesIt()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe);

        SetBucket(dbe, id, 9, CommitDiscipline.Commit);
        SetHp(dbe, id, 777, CommitDiscipline.TickFence);
        dbe.WriteTickFence(1);

        AssertHpIndexed(dbe, id, 777, 500, "after the fence");
        Assert.That(ClusterTransientIndexTests.TransientIndexed(dbe, 9), Is.EquivalentTo(new[] { id }), "the Transient tree, new bucket");
    }

    /// <summary>
    /// #1357's sequence, staged in the capture's window, keys read and no mark set: <paramref name="publish"/> runs there. The capture holds the slot's index
    /// lock across the two, so the publish finds the mark and leaves the move to the fence. Unlocked, it completes in the window and the fence departs a key
    /// the tree no longer holds.
    /// </summary>
    private static void PublishInTheCaptureWindow(DatabaseEngine dbe, ushort archetypeId, Action capture, Action publish)
    {
        var state = dbe._archetypeStates[archetypeId].ClusterState;
        Task committing = null;
        using var publishWaits = new ManualResetEventSlim();
        state.SlotIndexLockWaitProbe = publishWaits.Set;
        state.BeforeCaptureMarkProbe = () =>
        {
            state.BeforeCaptureMarkProbe = null;
            committing = Task.Run(publish);
            while (!publishWaits.IsSet && !committing.IsCompleted)
            {
                Thread.Yield();
            }
        };

        capture();
        Assert.That(committing, Is.Not.Null, "premise: the capture ran");
        committing.GetAwaiter().GetResult();
        state.SlotIndexLockWaitProbe = null;
    }

    /// <summary>Spawns the unique subject on <paramref name="archetypeId"/>'s fixture and publishes <c>Code = 777</c> in its capture window. Unlocked, the
    /// fence's
    /// departure misses and the entity's arrival collides with itself.</summary>
    private static void AssertCaptureRace<TArch>(DatabaseEngine dbe, ushort archetypeId, EntityId id, Comp<MixDiscUniqState> state, Action capture)
        where TArch : Archetype<TArch>
    {
        PublishInTheCaptureWindow(dbe, archetypeId, capture, () =>
        {
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
            tx.OpenMut(id).Set(state, new MixDiscUniqState { Code = 777 });
            Assert.That(tx.Commit(), Is.True);
        });

        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData<TArch>(dbe, "after the fence");
    }

    /// <summary>Two marks: the TickFence write captures through <c>ClusterRef.Set</c>, single or batched, or through <c>EntityRefMut.Set</c>.</summary>
    [TestCase(0, TestName = "ACaptureRacingACommitPublish_ThroughTheEntity")]
    [TestCase(1, TestName = "ACaptureRacingACommitPublish_ThroughTheCluster")]
    [TestCase(2, TestName = "ACaptureRacingACommitPublish_ThroughTheBatchedSet")]
    [CancelAfter(10_000)]
    [VerifiesRule("IX-06")]
    public void ACaptureRacingACommitPublish_TheFenceMovesOffTheKeyTheTreeHolds(int path)
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<MixDiscUniqArch>(MixDiscUniqArch.State.Set(new MixDiscUniqState { Code = 500 }), MixDiscUniqArch.Runtime.Set(new TIdxRuntime(1, 0)));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);

        AssertCaptureRace<MixDiscUniqArch>(dbe, Archetype<MixDiscUniqArch>.Metadata.ArchetypeId, id, MixDiscUniqArch.State, () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            if (path == 0)
            {
                tx.OpenMut(id).Set(MixDiscUniqArch.Runtime, new TIdxRuntime(9, 1));
            }
            else
            {
                Span<TIdxRuntime> values = stackalloc TIdxRuntime[64];
                var accessor = tx.For<MixDiscUniqArch>();
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var slots = cluster.OccupancyBits;
                    if (path == 1)
                    {
                        cluster.Set(MixDiscUniqArch.Runtime, BitOperations.TrailingZeroCount(slots), new TIdxRuntime(9, 1));
                    }
                    else
                    {
                        values[BitOperations.TrailingZeroCount(slots)] = new TIdxRuntime(9, 1);
                        cluster.Set(MixDiscUniqArch.Runtime, slots, values);
                    }
                }
            }

            Assert.That(tx.Commit(), Is.True);
        });
    }

    /// <summary>One mark: every fence-indexed slot is SingleVersion, and the TickFence write is to the entity's other component.</summary>
    [TestCase(false, TestName = "ACaptureRacingACommitPublish_OneMark_ThroughTheEntity")]
    [TestCase(true, TestName = "ACaptureRacingACommitPublish_OneMark_ThroughTheBatchedSet")]
    [CancelAfter(10_000)]
    [VerifiesRule("IX-06")]
    public void ACaptureRacingACommitPublish_OneMark_TheFenceMovesOffTheKeyTheTreeHolds(bool batched)
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<MixDiscOneMarkArch>(MixDiscOneMarkArch.State.Set(new MixDiscUniqState { Code = 500 }),
                MixDiscOneMarkArch.Other.Set(new MixDiscOtherState { Group = 1 }));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);

        AssertCaptureRace<MixDiscOneMarkArch>(dbe, Archetype<MixDiscOneMarkArch>.Metadata.ArchetypeId, id, MixDiscOneMarkArch.State, () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            if (!batched)
            {
                tx.OpenMut(id).Set(MixDiscOneMarkArch.Other, new MixDiscOtherState { Group = 2 });
            }
            else
            {
                Span<MixDiscOtherState> values = stackalloc MixDiscOtherState[64];
                var accessor = tx.For<MixDiscOneMarkArch>();
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var slots = cluster.OccupancyBits;
                    values[BitOperations.TrailingZeroCount(slots)] = new MixDiscOtherState { Group = 2 };
                    cluster.Set(MixDiscOneMarkArch.Other, slots, values);
                }
            }

            Assert.That(tx.Commit(), Is.True);
        });
    }

    /// <summary>The other order: the TickFence transaction records the SingleVersion key, then the Commit transaction moves the same field at its
    /// publish. The fence must not move it a second time from the key it recorded.</summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void TickFenceMovesSingleVersion_ThenCommitMovesItAgain_TheIndexEndsOnTheLastKey()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe);

        SetHp(dbe, id, 600, CommitDiscipline.TickFence);
        SetHp(dbe, id, 777, CommitDiscipline.Commit);
        dbe.WriteTickFence(1);

        AssertHpIndexed(dbe, id, 777, 500, "after the fence");
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<TIdxMixedArch>().WhereField<TIdxState>(s => s.Hp == 600).Execute(), Is.Empty, "the intermediate Hp");
    }

    /// <summary>The Commit transaction moves the SingleVersion key first, then the TickFence one moves it again.</summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void CommitMovesSingleVersion_ThenTickFenceMovesItAgain_TheIndexEndsOnTheLastKey()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe);

        SetHp(dbe, id, 600, CommitDiscipline.Commit);
        SetHp(dbe, id, 777, CommitDiscipline.TickFence);
        dbe.WriteTickFence(1);

        AssertHpIndexed(dbe, id, 777, 500, "after the fence");
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Query<TIdxMixedArch>().WhereField<TIdxState>(s => s.Hp == 600).Execute(), Is.Empty, "the intermediate Hp");
    }

    private static void Destroy(DatabaseEngine dbe, EntityId id, CommitDiscipline discipline)
    {
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, discipline);
        tx.Destroy(id);
        Assert.That(tx.Commit(), Is.True);
    }

    private static void AssertGone(DatabaseEngine dbe, EntityId id, string when)
    {
        IndexDataOracle.AssertIndexAgreesWithData<TIdxMixedArch>(dbe, when);
        Assert.That(ClusterTransientIndexTests.TransientIndexed(dbe, 1), Does.Not.Contain(id), $"{when}: the Transient tree");
        Assert.That(ClusterTransientIndexTests.TransientIndexed(dbe, 9), Does.Not.Contain(id), $"{when}: the Transient tree");
    }

    /// <summary>The Commit transaction marks the entity, then a TickFence destroy defers every fence-maintained slot: the SingleVersion one must have been
    /// recorded too, or its entry outlives the slot.</summary>
    [Test]
    [VerifiesRule("IX-06")]
    public void CommitWritesTransient_ThenTickFenceDestroys_NoEntryOutlivesTheSlot()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe);

        SetBucket(dbe, id, 9, CommitDiscipline.Commit);
        Destroy(dbe, id, CommitDiscipline.TickFence);
        dbe.WriteTickFence(1);

        AssertGone(dbe, id, "after the fence");
    }

    /// <summary>A TickFence move leaves the tree on the recorded key, not HEAD's; a Commit destroy then removing the SingleVersion entry inline by HEAD's key
    /// would miss it.</summary>
    [Test]
    [VerifiesRule("IX-06")]
    public void TickFenceMovesSingleVersion_ThenCommitDestroys_NoEntryOutlivesTheSlot()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe);

        SetHp(dbe, id, 777, CommitDiscipline.TickFence);
        Destroy(dbe, id, CommitDiscipline.Commit);
        dbe.WriteTickFence(1);

        AssertGone(dbe, id, "after the fence");
    }

    /// <summary>One Commit transaction changes a Transient key and a SingleVersion key of the entity: its capture marks the entity, so the fence, not the
    /// publish, moves the SingleVersion key.</summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void OneCommitTransactionMovesBothKeys_TheIndexFollowsBoth()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit))
        {
            var e = tx.OpenMut(id);
            e.Set(TIdxMixedArch.State, new TIdxState(777));
            e.Set(TIdxMixedArch.Runtime, new TIdxRuntime(9, 1));
            Assert.That(tx.Commit(), Is.True);
        }

        // The Commit discipline's promise (QFENCE): the SingleVersion tree is on the new key at commit, though the Transient write marked the entity.
        AssertHpIndexed(dbe, id, 777, 500, "after the commit, before the fence");
        dbe.WriteTickFence(1);
        AssertHpIndexed(dbe, id, 777, 500, "after the fence");
        Assert.That(ClusterTransientIndexTests.TransientIndexed(dbe, 9), Is.EquivalentTo(new[] { id }), "the Transient tree, new bucket");
    }

    /// <summary>A Commit write after a TickFence move of the same field leaves the tree to the fence, inside the TickFence window: a query in the window
    /// returns no row its current value fails, and may miss the new value until the fence (QFENCE-01).</summary>
    [Test]
    [VerifiesRule("QFENCE-01")]
    public void TickFenceThenCommitMoves_AQueryBeforeTheFence_AnswersTheCurrentValue()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe);

        SetHp(dbe, id, 600, CommitDiscipline.TickFence);
        SetHp(dbe, id, 777, CommitDiscipline.Commit);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TIdxMixedArch>().WhereField<TIdxState>(s => s.Hp == 777).Execute(), Is.SubsetOf(new[] { id }), "the current Hp");
            Assert.That(tx.Query<TIdxMixedArch>().WhereField<TIdxState>(s => s.Hp == 600).Execute(), Is.Empty, "the intermediate Hp");
            Assert.That(tx.Query<TIdxMixedArch>().WhereField<TIdxState>(s => s.Hp == 500).Execute(), Is.Empty, "the pre-tick Hp");
        }

        dbe.WriteTickFence(1);
        AssertHpIndexed(dbe, id, 777, 500, "after the fence");
    }

    /// <summary>The issue's sequence on a unique SingleVersion index: the tree must name the entity under its new code after the fence, and free the old
    /// one for another entity to take.</summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void Unique_CommitWritesTransient_ThenTickFenceMovesSingleVersion_TheFenceMovesIt()
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Others; i++)
            {
                tx.Spawn<MixDiscUniqArch>(MixDiscUniqArch.State.Set(new MixDiscUniqState { Code = i }),
                    MixDiscUniqArch.Runtime.Set(new TIdxRuntime(i % 4, 0)));
            }

            id = tx.Spawn<MixDiscUniqArch>(MixDiscUniqArch.State.Set(new MixDiscUniqState { Code = 500 }), MixDiscUniqArch.Runtime.Set(new TIdxRuntime(1, 0)));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit))
        {
            tx.OpenMut(id).Set(MixDiscUniqArch.Runtime, new TIdxRuntime(9, 1));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Set(MixDiscUniqArch.State, new MixDiscUniqState { Code = 777 });
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData<MixDiscUniqArch>(dbe, "after the fence");

        // The old code is free: another entity takes it at the next fence without a collision.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<MixDiscUniqArch>(MixDiscUniqArch.State.Set(new MixDiscUniqState { Code = 500 }), MixDiscUniqArch.Runtime.Set(new TIdxRuntime(2, 0)));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(2);
        IndexDataOracle.AssertIndexAgreesWithData<MixDiscUniqArch>(dbe, "after a newcomer took the old code");
    }
}
