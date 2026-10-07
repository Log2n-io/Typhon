using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// DatabaseEngine.DurableLsn — the public read of how far durability has actually got.
//
// It exists because "has my write reached the disk yet?" had no public answer. GetWalTotalBytes() is public and answers in SEGMENT-sized steps, so it cannot
// distinguish a tick that persisted something from one that did not: a preallocated segment absorbs thousands of records without changing size. That is not a
// hypothetical — the SWG demo needed exactly this distinction to show that its checkpoint-durable archetypes emit nothing while its one Versioned component
// emits per tick, and GetWalTotalBytes could not show it.
//
// Own archetypes, because ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720).
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.DurLsn.Walled", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DurLsnWalledData
{
    [Field]
    public int Value;
}

[Component("Typhon.Test.DurLsn.Ckpt", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DurLsnCkptData
{
    [Field]
    public int Value;
}

/// <summary>Default durability: its fence appends WAL records, so the watermark must move for it.</summary>
[Archetype]
partial class DurLsnWalled : Archetype<DurLsnWalled>
{
    public static readonly Comp<DurLsnWalledData> Data = Register<DurLsnWalledData>();
}

/// <summary>Checkpoint-granular durability: its fence appends nothing, so the watermark must NOT move for it.</summary>
[Archetype(ClusterDurability = ClusterDurability.Checkpoint)]
partial class DurLsnCkpt : Archetype<DurLsnCkpt>
{
    public static readonly Comp<DurLsnCkptData> Data = Register<DurLsnCkptData>();
}

[TestFixture]
[NonParallelizable]
class DurableLsnAccessorTests : TestBase<DurableLsnAccessorTests>
{
    private const int EntityCount = 64;

    /// <summary>
    /// How long to spin waiting for the writer thread to publish. The watermark advances asynchronously, so a bound is needed; it is a SPIN and not a
    /// <c>SpinWait.SpinUntil</c>, which sleeps in 15.6 ms slices and would turn a microsecond wait into a quantised one.
    /// </summary>
    private const int SpinIterations = 20_000_000;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<DurLsnWalledData>();
        dbe.RegisterComponentFromAccessor<DurLsnCkptData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// The accessor reaches the writer's own watermark: after a fence that appended records, it catches up to that fence's LSN and never exceeds what was
    /// appended.
    /// </summary>
    /// <remarks>
    /// Asserted against <c>WriteTickFence</c>'s RETURN VALUE — the LSN the fence published — rather than against another reading of the same accessor. An
    /// accessor compared only with itself can be wrong in both places at once, which is the mistake the sibling
    /// <c>PublicArchetypeIdTests</c> records making and correcting.
    /// </remarks>
    [Test]
    public void DurableLsn_CatchesUpToAFencesPublishedLsn()
    {
        using var dbe = SetupEngine();
        var before = dbe.DurableLsn;

        var ids = SpawnWalled(dbe);
        var fenceLsn = MutateAndFenceWalled(dbe, tick: 1, ids);

        Assert.That(fenceLsn, Is.GreaterThan(0), "precondition: a default-durability archetype's fence must publish records");

        var reached = SpinUntilDurable(dbe, fenceLsn);

        Assert.Multiple(() =>
        {
            Assert.That(reached, Is.True, $"the durable LSN never reached the fence's {fenceLsn} (stalled at {dbe.DurableLsn})");
            Assert.That(dbe.DurableLsn, Is.GreaterThanOrEqualTo(before), "the watermark must never go backwards");
        });
    }

    /// <summary>
    /// The control, and the half that carries the meaning: a fence that appended NOTHING leaves the watermark where it was. Without it, an accessor that
    /// returned a monotonically rising clock would pass the test above.
    /// </summary>
    [Test]
    public void DurableLsn_DoesNotMoveForAFenceThatAppendedNothing()
    {
        using var dbe = SetupEngine();

        // Settle first: spawns and the archetype build itself append, and the watermark must be quiet before "did it move?" means anything.
        var walled = SpawnWalled(dbe);
        var ckpt = SpawnCkpt(dbe);
        var settle = MutateAndFenceWalled(dbe, tick: 1, walled);
        SpinUntilDurable(dbe, settle);
        Thread.Sleep(50);
        var quiet = dbe.DurableLsn;

        // A tick that dirties ONLY the checkpoint-durable archetype. Its fence publishes no records (#568), so there is nothing for the writer to make durable.
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in ckpt)
            {
                var entity = tx.OpenMut(id);
                var data = entity.Read(DurLsnCkpt.Data);
                data.Value++;
                entity.Set(DurLsnCkpt.Data, data);
            }

            tx.Commit();
        }

        var ckptLsn = dbe.WriteTickFence(2);
        Thread.Sleep(50);

        Assert.Multiple(() =>
        {
            Assert.That(ckptLsn, Is.Zero, "precondition (#568): a Checkpoint archetype's fence publishes no WAL records");
            Assert.That(dbe.DurableLsn, Is.EqualTo(quiet),
                "the watermark moved on a tick that appended nothing: it is not reading the WAL writer, or something else is appending");
        });
    }

    private static bool SpinUntilDurable(DatabaseEngine dbe, long target)
    {
        for (var i = 0; i < SpinIterations; i++)
        {
            if (dbe.DurableLsn >= target)
            {
                return true;
            }

            Thread.SpinWait(4);
        }

        return dbe.DurableLsn >= target;
    }

    private static EntityId[] SpawnWalled(DatabaseEngine dbe)
    {
        var ids = new EntityId[EntityCount];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var data = new DurLsnWalledData { Value = i };
            ids[i] = tx.Spawn<DurLsnWalled>(DurLsnWalled.Data.Set(in data));
        }

        tx.Commit();
        return ids;
    }

    private static EntityId[] SpawnCkpt(DatabaseEngine dbe)
    {
        var ids = new EntityId[EntityCount];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var data = new DurLsnCkptData { Value = i };
            ids[i] = tx.Spawn<DurLsnCkpt>(DurLsnCkpt.Data.Set(in data));
        }

        tx.Commit();
        return ids;
    }

    private static long MutateAndFenceWalled(DatabaseEngine dbe, long tick, EntityId[] ids)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in ids)
            {
                var opened = tx.OpenMut(id);
                var dataCopy = opened.Read(DurLsnWalled.Data);
                dataCopy.Value++;
                opened.Set(DurLsnWalled.Data, dataCopy);
            }

            tx.Commit();
        }

        return dbe.WriteTickFence(tick);
    }
}
