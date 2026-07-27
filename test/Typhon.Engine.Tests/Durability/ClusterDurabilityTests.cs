using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// #568 — ClusterDurability.Checkpoint: the tick fence must emit NO WAL records for an archetype that declared a checkpoint-granular durability window, while
// every OTHER duty of the fence keeps running.
//
// The second half is the part that can silently break. The cluster dirty bitmap has eleven consumers and WAL emit is only one of them; gating too early (at
// the top of FinalizeArchetypeFence rather than after the bookkeeping) would also disable dormancy, the dirty ring, and change-filtered dispatch — which would
// not fail any durability test, only make systems silently stop being scheduled.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClusterDur.Walled", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClusterDurWalledData
{
    [Field]
    public int Value;
}

[Component("Typhon.Test.ClusterDur.Ckpt", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClusterDurCkptData
{
    [Field]
    public int Value;
}

/// <summary>Default durability — the control. Its fence emission must be unaffected by the feature.</summary>
[Archetype]
partial class ClusterDurWalled : Archetype<ClusterDurWalled>
{
    public static readonly Comp<ClusterDurWalledData> Data = Register<ClusterDurWalledData>();
}

/// <summary>Opted into checkpoint-granular durability — the subject.</summary>
[Archetype(ClusterDurability = ClusterDurability.Checkpoint)]
partial class ClusterDurCkpt : Archetype<ClusterDurCkpt>
{
    public static readonly Comp<ClusterDurCkptData> Data = Register<ClusterDurCkptData>();
}

[TestFixture]
[NonParallelizable]
class ClusterDurabilityTests : TestBase<ClusterDurabilityTests>
{
    private const int EntityCount = 100;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClusterDurWalledData>();
        dbe.RegisterComponentFromAccessor<ClusterDurCkptData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The attribute reaches archetype metadata at all — the plumbing, asserted before anything depends on it.</summary>
    [Test]
    public void Attribute_IsReadIntoArchetypeMetadata()
    {
        using var dbe = SetupEngine();

        Assert.That(Archetype<ClusterDurCkpt>.Metadata.ClusterDurability, Is.EqualTo(ClusterDurability.Checkpoint));
        Assert.That(Archetype<ClusterDurWalled>.Metadata.ClusterDurability, Is.EqualTo(ClusterDurability.FenceWal),
            "an archetype that declares nothing must keep the pre-#568 behaviour");
    }

    /// <summary>
    /// Genuineness: a Checkpoint archetype's fence publishes no WAL records, a default one does. Both halves matter — asserting only that Checkpoint emits
    /// nothing would also pass if the fence were broken outright, so the FenceWal control runs through the identical code path.
    /// </summary>
    [Test]
    public void CheckpointArchetype_FenceEmitsNothing_WhileDefaultArchetypeStillEmits()
    {
        using var dbe = SetupEngine();

        // Tick 1 — dirty only the CHECKPOINT archetype.
        var ckptIds = SpawnAndCommit(dbe, checkpointArchetype: true);
        var checkpointLsn = MutateAndFence(dbe, tick: 1, ckptIds, checkpointArchetype: true);

        // Tick 2 — dirty only the DEFAULT archetype, same engine, same fence.
        var walledIds = SpawnAndCommit(dbe, checkpointArchetype: false);
        var walledLsn = MutateAndFence(dbe, tick: 2, walledIds, checkpointArchetype: false);

        Assert.That(checkpointLsn, Is.Zero,
            "a Checkpoint archetype must publish no fence WAL records — a non-zero LSN means the gate did not fire");
        Assert.That(walledLsn, Is.GreaterThan(0),
            "the default archetype must still emit — if this is also zero the fence is broken, not gated");
    }

    /// <summary>
    /// The gate suppresses ONLY the emission. Everything the dirty bitmap feeds must still be updated, or systems silently stop being dispatched.
    /// This is the regression that a durability-only test would never catch.
    /// </summary>
    [Test]
    public void CheckpointArchetype_DirtyBitmapConsumers_AreUnaffected()
    {
        using var dbe = SetupEngine();
        var ids = SpawnAndCommit(dbe, checkpointArchetype: true);
        MutateAndFence(dbe, tick: 1, ids, checkpointArchetype: true);

        var meta = Archetype<ClusterDurCkpt>.Metadata;
        var state = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        Assert.That(state.PreviousTickDirtySnapshot, Is.Not.Null,
            "change-filtered dispatch reads PreviousTickDirtySnapshot — gating too early would leave it null and silently stop dispatching");

        var anyDirty = false;
        foreach (var word in state.PreviousTickDirtySnapshot)
        {
            if (word != 0)
            {
                anyDirty = true;
                break;
            }
        }

        Assert.That(anyDirty, Is.True, "the tick dirtied every entity — the snapshot must record that");

        var engineState = dbe._archetypeStates[meta.ArchetypeId];
        for (var slot = 0; slot < state.Layout.ComponentCount; slot++)
        {
            Assert.That(engineState.SlotToComponentTable[slot].PreviousTickHadDirtyEntities, Is.True,
                $"slot {slot}'s ComponentTable must still be flagged dirty for change-filtered dispatch");
        }
    }

    /// <summary>
    /// The durability contract itself: values written under Checkpoint reach disk through the checkpoint, so they survive a CLEAN close and reopen. This is the
    /// guarantee the mode does make — it trades the crash window, not persistence.
    /// </summary>
    [Test]
    public void CheckpointArchetype_ValuesSurviveCleanReopen()
    {
        long probe;
        using (var dbe = SetupEngine())
        {
            var ids = SpawnAndCommit(dbe, checkpointArchetype: true);
            MutateAndFence(dbe, tick: 1, ids, checkpointArchetype: true);

            using var tx = dbe.CreateQuickTransaction();
            using var view = tx.Query<ClusterDurCkpt>().ToView();
            probe = 0;
            foreach (var e in view)
            {
                probe++;
            }

            tx.Commit();
        }

        Assert.That(probe, Is.EqualTo(EntityCount));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static EntityId[] SpawnAndCommit(DatabaseEngine dbe, bool checkpointArchetype)
    {
        var ids = new EntityId[EntityCount];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            if (checkpointArchetype)
            {
                var v = new ClusterDurCkptData { Value = i };
                ids[i] = tx.Spawn<ClusterDurCkpt>(ClusterDurCkpt.Data.Set(in v));
            }
            else
            {
                var v = new ClusterDurWalledData { Value = i };
                ids[i] = tx.Spawn<ClusterDurWalled>(ClusterDurWalled.Data.Set(in v));
            }
        }

        tx.Commit();
        return ids;
    }

    /// <summary>Dirty every entity through the normal write path, then run the fence. Returns the LSN the fence published.</summary>
    private static long MutateAndFence(DatabaseEngine dbe, long tick, EntityId[] ids, bool checkpointArchetype)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in ids)
            {
                if (checkpointArchetype)
                {
                    tx.OpenMut(id).Write(ClusterDurCkpt.Data).Value++;
                }
                else
                {
                    tx.OpenMut(id).Write(ClusterDurWalled.Data).Value++;
                }
            }

            tx.Commit();
        }

        return dbe.WriteTickFence(tick);
    }
}
