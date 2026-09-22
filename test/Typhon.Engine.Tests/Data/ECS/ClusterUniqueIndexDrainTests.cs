using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #886 lead C. Two entities in two clusters written to the same key of a unique cluster index inside one tick: the shadow drain must reject exactly one of
/// them with #675's message, and the tree must come out holding the key once, with the loser's old key still where it was. Before #886 the drain proved this
/// with a <c>TryGet</c> before the <c>Move</c>; now it reads <c>BTree.Move</c>'s own verdict, taken under the leaf latch, which is the form that stays true
/// when two workers drain two clusters at once.
/// </summary>
/// <remarks>
/// No fixture in this repo declared a unique cluster index and drove two colliding writes through one drain before this one — the #882 drain-order commit
/// says so in its own comment — so which of the two entities is rejected had never been pinned. It still is not: the assertion is symmetric, because a
/// sliced drain makes the winner the first slice to reach the tree.
/// </remarks>
[TestFixture]
class ClusterUniqueIndexDrainTests : TestBase<ClusterUniqueIndexDrainTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const int Entities = 128;
    private const int AlphaBase = 1000;
    private const int BetaBase = 2000;
    private const int CollidingKey = 777;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<DirKeyAlpha>();
        dbe.RegisterComponentFromAccessor<DirKeyBeta>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Ablation: discard <c>Move</c>'s return value at the drain site — no exception is raised and the second assertion below reddens.</summary>
    [Test]
    public void TwoClustersWrittenToOneUniqueKeyInOneTick_RejectExactlyOne_AndTheTreeHoldsTheKeyOnce()
    {
        var dbe = SetupEngine();
        var ids = new EntityId[Entities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Entities; i++)
            {
                ids[i] = tx.Spawn<DirKeyArch>(DirKeyArch.Alpha.Set(new DirKeyAlpha(AlphaBase + i, i)), DirKeyArch.Beta.Set(new DirKeyBeta(BetaBase + i, i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Entity 0 and entity 64 sit in different clusters (a cluster holds at most 64), and both take the same Alpha.Code in one tick.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(ids[0]).Write(DirKeyArch.Alpha).Code = CollidingKey;
            tx.OpenMut(ids[64]).Write(DirKeyArch.Alpha).Code = CollidingKey;
            tx.Commit();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => dbe.WriteTickFence(2), "a unique index cannot hold two entities under one key");
        Assert.That(ex.Message, Does.Contain("unique index collision"), "#675's message, unchanged");

        // The tree: the key is held exactly once, the winner's old key is gone, the loser's old key is untouched — whichever of the two won.
        var (hasColliding, hasOld0, hasOld64) = ProbeAlphaCode(dbe, CollidingKey, AlphaBase, AlphaBase + 64);
        Assert.That(hasColliding, Is.True, "the first entity to reach the tree holds the key");
        // The optimistic arms of Move refuse without touching the tree, so the loser keeps its old key. The pessimistic arm removes the old key before
        // its insert throws (BTree.Move.cs MovePessimistic); it needs MaxOptimisticRestarts failures first, unreachable single-threaded, so this pins the
        // optimistic outcome only.
        Assert.That(hasOld0 ^ hasOld64, Is.True, $"exactly one old key must survive: old0={hasOld0} old64={hasOld64}");
    }

    /// <summary>Two entities in two clusters moving to two DIFFERENT keys in one tick is the ordinary case and must not be mistaken for a collision.</summary>
    [Test]
    public void TwoClustersWrittenToDistinctKeysInOneTick_BothMove()
    {
        var dbe = SetupEngine();
        var ids = new EntityId[Entities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Entities; i++)
            {
                ids[i] = tx.Spawn<DirKeyArch>(DirKeyArch.Alpha.Set(new DirKeyAlpha(AlphaBase + i, i)), DirKeyArch.Beta.Set(new DirKeyBeta(BetaBase + i, i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(ids[0]).Write(DirKeyArch.Alpha).Code = CollidingKey;
            tx.OpenMut(ids[64]).Write(DirKeyArch.Alpha).Code = CollidingKey + 1;
            tx.Commit();
        }

        Assert.DoesNotThrow(() => dbe.WriteTickFence(2));
        var (hasA, hasOld0, hasOld64) = ProbeAlphaCode(dbe, CollidingKey, AlphaBase, AlphaBase + 64);
        var (hasB, _, _) = ProbeAlphaCode(dbe, CollidingKey + 1, AlphaBase, AlphaBase + 64);
        Assert.That(hasA && hasB, Is.True, "both new keys are in the tree");
        Assert.That(hasOld0 || hasOld64, Is.False, "neither old key survives");
        IndexDataOracle.AssertIndexAgreesWithData<DirKeyArch>(dbe, "after two distinct moves");
    }

    /// <summary>Looks the three keys up in Alpha.Code's tree, identified by a sentinel key no test writes.</summary>
    private static unsafe (bool, bool, bool) ProbeAlphaCode(DatabaseEngine dbe, long a, long b, long c)
    {
        var clusterState = dbe._archetypeStates[DirKeyArch.Metadata.ArchetypeId].ClusterState;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var found = (false, false, false);
        for (var s = 0; s < clusterState.IndexSlots.Length; s++)
        {
            ref var ixSlot = ref clusterState.IndexSlots[s];
            for (var f = 0; f < ixSlot.Fields.Length; f++)
            {
                ref var field = ref ixSlot.Fields[f];
                if (field.Index == null || field.AllowMultiple)
                {
                    continue;
                }

                var acc = field.Index.Segment.CreateChunkAccessor();
                try
                {
                    // Alpha.Code and Beta.Code are both unique int indexes; only Alpha holds a key in [AlphaBase, AlphaBase + Entities). Entity 5 is never
                    // written by any test here, so its key identifies the field rather than relying on Beta's range never overlapping the probed keys.
                    var sentinel = (long)(AlphaBase + 5);
                    if (!field.Index.TryGet(&sentinel, ref acc).IsSuccess)
                    {
                        continue;
                    }

                    var ka = a;
                    var kb = b;
                    var kc = c;
                    found.Item1 |= field.Index.TryGet(&ka, ref acc).IsSuccess;
                    found.Item2 |= field.Index.TryGet(&kb, ref acc).IsSuccess;
                    found.Item3 |= field.Index.TryGet(&kc, ref acc).IsSuccess;
                }
                finally
                {
                    acc.Dispose();
                }
            }
        }

        return found;
    }
}
